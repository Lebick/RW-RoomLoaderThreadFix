using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace RoomPreparerFix;

/// <summary>
/// 버려진 RoomPreparer 스레드를 종료시킨다.
///
/// 원인: RoomPreparer.UpdateThread는 메인 스레드가 RoomPreparer.Update()를 불러 주기를
/// Sleep 없이 while(true)로 기다린다(status 0의 requestShortcutsReady, status 4의 requestReadyForAI).
/// 방 로딩 중에 게임이 종료되거나 world가 바뀌면 Update()가 다시 불리지 않아 스레드가 영원히 스핀한다.
///
/// 해결: 버려진 preparer의 status를 5로 바꾸면 스레드가 다음 루프에서 case 5로 들어가 정상 return 한다.
/// 스레드가 lock 밖에서 작업한 뒤 status++/status=4 로 값을 덮어쓸 수 있으므로,
/// 스레드가 실제로 끝날 때까지 매 프레임 다시 5로 설정한다.
/// </summary>
internal static class PreparerFix
{
    /// <summary>스레드가 실행 중인 preparer (백그라운드 스레드에서 추가/제거되므로 lock 필요)</summary>
    private static readonly HashSet<RoomPreparer> running = new();
    private static readonly object sync = new();

    /// <summary>종료 요청한 preparer — 메인 스레드에서만 접근</summary>
    private static readonly Dictionary<RoomPreparer, string> stopping = new();
    /// <summary>world 불일치가 처음 관측된 시각 — 메인 스레드에서만 접근</summary>
    private static readonly Dictionary<RoomPreparer, float> suspectSince = new();
    private static readonly ConditionalWeakTable<RainWorldGame, object> shutDownGames = new();

    /// <summary>world 불일치가 이 시간 이상 계속되면 버려진 것으로 판단 (전환 중 일시적 상태 배제)</summary>
    private const float WorldMismatchGraceSec = 5f;
    private const float ScanIntervalSec = 1f;
    private static float nextScan;

    public static int StoppedTotal;

    public static void Apply()
    {
        On.RoomPreparer.UpdateThread += RoomPreparer_UpdateThread;
        On.RainWorldGame.ShutDownProcess += RainWorldGame_ShutDownProcess;
    }

    public static void Remove()
    {
        On.RoomPreparer.UpdateThread -= RoomPreparer_UpdateThread;
        On.RainWorldGame.ShutDownProcess -= RainWorldGame_ShutDownProcess;
    }

    private static void RoomPreparer_UpdateThread(On.RoomPreparer.orig_UpdateThread orig, RoomPreparer self)
    {
        lock (sync) running.Add(self);
        try
        {
            orig(self);
        }
        finally
        {
            lock (sync) running.Remove(self);
        }
    }

    private static void RainWorldGame_ShutDownProcess(On.RainWorldGame.orig_ShutDownProcess orig, RainWorldGame self)
    {
        orig(self);
        try
        {
            if (!shutDownGames.TryGetValue(self, out _)) shutDownGames.Add(self, null);
            foreach (var p in RunningSnapshot())
            {
                if (p.room?.game == self) RequestStop(p, "game shut down");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(ex);
        }
    }

    private static List<RoomPreparer> RunningSnapshot()
    {
        lock (sync) return new List<RoomPreparer>(running);
    }

    private static bool IsRunning(RoomPreparer p)
    {
        lock (sync) return running.Contains(p);
    }

    private static void RequestStop(RoomPreparer p, string reason)
    {
        if (stopping.ContainsKey(p)) return;
        stopping[p] = reason;
        suspectSince.Remove(p);
        Plugin.Logger.LogInfo($"Stopping abandoned room loader thread: room={p.room?.abstractRoom?.name ?? "?"} status={p.status} reason={reason}");
    }

    /// <summary>매 프레임 메인 스레드에서 호출</summary>
    public static void Tick()
    {
        if (stopping.Count > 0) PushStops();

        float now = Time.realtimeSinceStartup;
        if (now < nextScan) return;
        nextScan = now + ScanIntervalSec;
        Scan(now);
    }

    private static void PushStops()
    {
        List<RoomPreparer> finished = null;
        foreach (var p in stopping.Keys)
        {
            bool done;
            lock (p)
            {
                done = p.threadFinished || !IsRunning(p);
                if (!done) p.status = 5;
            }
            if (done) (finished ??= new List<RoomPreparer>()).Add(p);
        }
        if (finished == null) return;
        foreach (var p in finished)
        {
            stopping.Remove(p);
            StoppedTotal++;
            Plugin.Logger.LogInfo($"Room loader thread stopped cleanly: room={p.room?.abstractRoom?.name ?? "?"} (total {StoppedTotal})");
        }
    }

    /// <summary>ShutDownProcess 훅에서 놓친 경우를 위한 안전망</summary>
    private static void Scan(float now)
    {
        var list = RunningSnapshot();
        var alive = new HashSet<RoomPreparer>(list);
        foreach (var p in list)
        {
            if (stopping.ContainsKey(p)) continue;
            var game = p.room?.game;
            // game == null 은 지도/월드 생성 등에서 동기적으로 쓰는 preparer → 건드리지 않음
            if (game == null) continue;

            if (shutDownGames.TryGetValue(game, out _))
            {
                RequestStop(p, "belongs to a shut-down game");
                continue;
            }

            if (IsOrphanedByWorldChange(p, game))
            {
                if (!suspectSince.TryGetValue(p, out float since)) suspectSince[p] = now;
                else if (now - since >= WorldMismatchGraceSec) RequestStop(p, "belongs to a previous world (region change)");
            }
            else
            {
                suspectSince.Remove(p);
            }
        }

        // 이미 끝난 스레드의 의심 기록 정리
        if (suspectSince.Count > 0)
        {
            var stale = new List<RoomPreparer>();
            foreach (var p in suspectSince.Keys) if (!alive.Contains(p)) stale.Add(p);
            foreach (var p in stale) suspectSince.Remove(p);
        }
    }

    /// <summary>
    /// preparer가 속한 world가 더 이상 게임의 활성 world가 아니고, 활성 world의 loadingRooms에도 없으면
    /// 아무도 Update()를 불러 주지 않는다. (World 객체는 지역 이동마다 새로 만들어지고 재사용되지 않음)
    /// </summary>
    private static bool IsOrphanedByWorldChange(RoomPreparer p, RainWorldGame game)
    {
        var active = game.overWorld?.activeWorld;
        if (active == null || p.room.world == active) return false;
        return active.loadingRooms == null || !active.loadingRooms.Contains(p);
    }
}
