# Room Loader Thread Fix

[English](README.md) | **한국어**

저장(동면, 메아리 조우, 워프 등)을 할 때마다 CPU 사용량이 늘어나 재시작 전까지 유지되는 Rain World 바닐라 버그를 수정하는 BepInEx 모드.
Watcher 캠페인에서 특히 심하며, 오래 플레이하면 CPU가 100% 가까이 올라가 게임이 끊긴다.

- 대상 게임 버전: v1.11.8 (Watcher DLC 포함), BepInEx 5 (Remix 기본 탑재)
- 의존성 없음, 다른 모드와 함께 사용 가능
- 게임 로직이나 저장 데이터는 바꾸지 않는다 (아래 "게임 플레이 영향" 참고)

## 설치
1. [Releases](../../releases)에서 `roompreparerfix-<버전>.zip`을 받는다.
2. `Rain World/RainWorld_Data/StreamingAssets/mods/`에 압축을 푼다 → `mods/roompreparerfix/modinfo.json`이 되도록.
3. 게임 실행 → Remix 메뉴에서 **Room Loader Thread Fix** 활성화 → 재시작.

## 원인 (v1.11.8 기준, 실측 확인)
- 방을 불러오는 `RoomPreparer`는 백그라운드 스레드(`UpdateThread`)에서 일한다. 중간 단계마다 메인 스레드가 `RoomPreparer.Update()`로 응답해 주기를 **Sleep 없이 `while(true)`로 기다린다.**
  - `status == 0`: `requestShortcutsReady` 처리 대기
  - `status == 4`: `requestReadyForAI` 처리 → `status = 5` 대기
- 메인 스레드의 응답은 `RainWorldGame.Update`가 현재 world의 `loadingRooms`를 처리할 때만 일어난다.
- 그런데 방 로딩 도중 게임이 종료되면(저장 → 수면/메아리 화면) 아무도 `Update()`를 부르지 않는다. 그러면 스레드가 **영원히 코어 하나를 100% 점유한다.** 이런 스레드는 저장할 때마다 누적되고, 메뉴로 나가도 사라지지 않는다.
- 실측 결과(Watcher 캠페인, 진단 모드 사용): 저장 4번에 고아 스레드 3개가 생겼고, 각각 약 99%를 사용했다(프로세스 CPU 314% = 코어 3개 이상). 같은 플레이를 이 모드와 함께 했을 때는 저장 7번 동안 고아 스레드 3개가 생겼지만 모두 즉시 종료되었다(방 로딩 스레드 시작 167 / 종료 167).
- Watcher는 지역 이동과 저장이 동시에 일어나는 일이 잦아서(메아리, 워프) 특히 심하다.

## 수정 방식
- `RoomPreparer.UpdateThread`를 감싸서, 스레드가 실행 중인 preparer를 추적한다.
- 버려진 preparer를 찾으면 `status = 5`로 설정한다. 스레드는 다음 루프에서 `case 5`로 들어가 스스로 정상 `return` 한다.
  스레드가 lock 밖에서 `status`를 덮어쓸 수 있으므로, 실제로 끝날 때까지 매 프레임 다시 설정한다.
- 버려진 것으로 판단하는 조건:
  1. `RainWorldGame.ShutDownProcess` 직후, 그 게임에 속한 preparer → 즉시 종료
  2. (1초마다 점검하는 안전망) 종료된 게임 소속이거나, 이전 world 소속이면서 현재 world의 `loadingRooms`에도 없는 상태가 5초 이상 계속될 때
- `room.game == null`인 preparer(지도/월드 생성용 동기 사용)와 정상적으로 로딩 중인 preparer는 건드리지 않는다.
- 동작 기록: `BepInEx/LogOutput.log`에 `Stopping abandoned room loader thread` / `stopped cleanly`로 남는다.

## 게임 플레이 영향
이 모드는 **바닐라에서도 절대 완료되지 않는 스레드만** 멈춘다. 게임이 보는 상태는 모드 유무와 관계없이 같다.
- 바닐라에서 방 로딩을 진행시키는 곳은 활성 world의 `loadingRooms`를 처리하는 `RainWorldGame.Update` 하나뿐이다. 종료된 게임이나 비활성화된 World(World 객체는 지역 이동마다 새로 만들어지고 재사용되지 않음)의 preparer는 다시는 진행되지 않는다.
- 스레드가 하는 작업(`ShortcutMapper`, `AImapper`, `AIdataPreprocessor`)은 자기 Room 객체에만 쓴다. static 상태나 RNG는 건드리지 않는다.
- 모드는 게임 함수를 호출하지 않는다(`ShortCutsReady`/`ReadyForAI`도 부르지 않음). 저장이 끝난 뒤(`ShutDownProcess` 이후)에만 개입하므로 저장 파일 내용도 같다.
- 지역에 다시 돌아오면 World는 지역 파일과 저장된 지역 상태로 새로 만들어진다. 버려진 Room의 데이터는 쓰이지 않는다.

효과는 버려진 스레드가 CPU를 계속 먹던 것이 사라지는 것, 즉 랙 감소뿐이다.
스피드런 기록에 사용해도 되는지는 각 리더보드 규칙에 따르므로, 기록 제출 전에 운영진에게 확인할 것.

## 빌드
.NET SDK(net48 타깃)와 Rain World 설치가 필요하다. 게임 경로가 다르면 `-p:RainWorldDir=...`로 지정한다.
```
dotnet build src/RoomPreparerFix.csproj -c Release
```
출력은 `mod/plugins/`이며, `mods/roompreparerfix`로 자동 복사된다(`-p:CopyToGame=false`로 끌 수 있음).

## 라이선스
MIT
