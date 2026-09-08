# Pity Loot (SPT 4.1)

> **원작자** — **Bakahashi** (MIT)
>
> 원본은 **SPT 3.11용 TypeScript 서버 모드**입니다. SPT 4.x는 서버가 C#으로 완전히 새로
> 쓰였고 **JS/TS 모드 로더가 아예 없어서**, 이건 포팅이 아니라 **TypeScript → C# 전체
> 재작성**입니다. 동작과 설정 파일은 원본과 같게 유지했습니다.

현재 기준 **SPT 4.1.5**. 서버 전용.

---

## 뭐 하는 모드냐

하드코어하게 플레이하는데 퀘스트/하이드아웃에 필요한 아이템이 안 나와서 지치는 경우,
또는 플리마켓 없이 하면서 나중에 쓸 아이템을 **전부** 쟁여둘 필요는 없게 하고 싶은 경우를
위한 모드입니다.

지금 진행 중인 퀘스트와 하이드아웃 업그레이드에 **아직 부족한** 아이템의 드랍률을,
그 과제를 시작한 뒤 **지난 레이드 수**(또는 **실제 경과 시간**)에 비례해서 점점 올려줍니다.
필요한 만큼 이미 갖고 있으면 그 아이템은 대상에서 빠집니다.

- **정적 컨테이너 루트** · **루즈 루트** · **정적 탄약** · **봇 인벤토리** 확률에 적용
- 퀘스트 목표가 **잠긴 문 뒤**에 있으면 그 **열쇠**도 같이 올려줍니다 (`questKeys.json`)
- **건스미스** 과제에 필요한 부품도 같이 올려줍니다 (`gunsmith.json`)
- 열쇠와 건스미스 부품은 애초에 안 나오는 컨테이너에는 **직접 추가**해 줍니다
- 퀘스트를 완료하거나 하이드아웃을 올리면 해당 pity는 **초기화**됩니다

---

## 설정

`config/config.json` — **원본과 키 이름·구조가 완전히 동일합니다.** 3.11에서 쓰던 파일을
그대로 복사해 넣어도 됩니다.

| 키 | 기본값 | 설명 |
|---|---|---|
| `enabled` | `true` | 끄면 루트 테이블에 손을 안 댑니다 |
| `debug` / `trace` | `false` | `trace` 는 확률 변경을 **전부** 찍습니다. 수십만 줄 |
| `appliesToQuests` / `appliesToHideout` | `true` | 어느 쪽을 pity 대상으로 볼지 |
| `increasesStack` | `true` | 두 과제가 같은 아이템을 요구할 때 **합산**(20%+80% = 100%). `false` 면 **최대값**(80%) |
| `includeScavRaids` | `true` | 스캐브 레이드도 레이드 수에 포함 |
| `onlyIncreaseOnFailedRaids` | `true` | 살아 나오면 카운터를 안 올림 |
| `includeKeys` | `true` | 퀘스트 열쇠 포함 |
| `keysAdditionalMultiplier` | `2.5` | 열쇠에만 추가로 곱하는 배수 |
| `includeGunsmith` | `true` | 건스미스 부품 포함 |
| `maxDropRateMultiplier` | `10` | 배수 상한 |
| `dropRateIncreaseType` | `"raid"` | `raid` 또는 `time` |
| `dropRateIncreasePerRaid` | `0.25` | 레이드당 +25% |
| `dropRateIncreasePerHour` | `0.05` | 시간당 +5% |
| `appliesToWishlist` | `false` | 위시리스트 아이템에 고정 배수 적용 |
| `wishlistMultipliers` | 전부 `5.0` | 카테고리별 고정 배수 |
| `excludeCollector` | `false` | 컬렉터 퀘스트 제외 |

`config/questKeys.json`, `config/gunsmith.json` 은 데이터 파일이라 **원본 그대로** 가져왔습니다.

---

## 4.1로 오면서 달라진 것

### 1. 언어가 바뀌었습니다

SPT 4.x 서버는 C#입니다. TypeScript 모드는 로드조차 되지 않습니다. 로직 약 1,300줄을
C#으로 다시 썼습니다. 알고리즘(요구사항 수집 → 재고 차감 → 배수 계산 → 확률 적용)은
원본과 같고, 아래 두 군데만 **의도적으로** 다르게 만들었습니다.

### 2. 루트 테이블 사본을 더 이상 들고 있지 않습니다

원본은 시작할 때 **원본 로케이션 테이블 전체를 따로 보관**하고, pity가 바뀔 때마다 거기서
새 테이블 객체를 통째로 만들어 `tables.locations` 에 갈아끼웠습니다. 기준이 되는 원래
확률을 가져올 데가 그것밖에 없었기 때문입니다 — 배수를 두 번 적용하면 확률이 복리로
불어나니까요.

4.1에는 그럴 필요가 없습니다. 로케이션 루트가 `LazyLoad<T>` 로 노출되고, 여기에
**`AddTransformer`** 가 있습니다. 디스크에서 값을 새로 읽을 때마다 실행되는 변환 함수를
등록하는 방식입니다. 그래서:

- 변환 함수는 **항상 디스크의 원본 확률에서 시작**합니다 → 복리 누적이 구조적으로 불가능
- pity가 바뀌면 `Clear()` 한 번이면 다음 조회 때 알아서 다시 계산됩니다
- **수백 MB짜리 루트 테이블 사본을 서버가 켜져 있는 내내 메모리에 들고 있지 않습니다**

정적 탄약과 봇 테이블은 lazy-load가 아니라서, 그쪽은 원본과 같은 방식(최초 1회 기준값
스냅샷 → 매번 기준값에서 다시 계산)을 씁니다.

### 3. pity 기록을 프로필 옆에 저장합니다

원본은 모드 폴더 안에 `database/pityTracker.json` 을 직접 만들어 **모든 프로필을 한 파일에**
넣었습니다. 4.1에는 모드용 프로필별 데이터 저장소(`ProfileDataService`)가 있어서 그걸
씁니다 — 프로필 하나당 블롭 하나, 프로필과 같이 관리되고 같이 삭제됩니다.

### 4. Harmony를 안 씁니다

원본이 쓰던 `container.afterResolution` 으로 `LocationController.generateAll` 을 감싸는 방식은
4.1에 없습니다. 대신 위의 `LazyLoad` 변환 함수 + 정적 라우터 4개로 끝납니다. **IL 패치가
하나도 없어서** 같은 지점을 건드리는 다른 모드와 부딪힐 일이 없습니다.

훅 4개는 원본과 동일한 지점입니다:

| 라우터 | 언제 | 하는 일 |
|---|---|---|
| `/client/game/start` | 게임 시작 | 이 프로필 기준으로 pity 기준선을 잡음 (레이드 수는 안 올림) |
| `/client/match/local/end` | 레이드 종료 | **레이드 카운터가 움직이는 유일한 지점.** 살아 나와도 재계산은 함 — 인레이드에서 퀘스트 아이템을 채웠으면 카운터와 무관하게 남은 요구사항이 바뀌기 때문 (원본 마지막 수정사항) |
| `/client/game/profile/items/moving` | 퀘스트 제출 / 하이드아웃 업그레이드 | 레이드 없이 요구사항이 끝나는 경우 |
| `/client/raid/configuration` | 레이드 직전 | 봇 테이블 재작성 (봇은 lazy-load가 아님) |

넷 다 **응답을 그대로 통과**시키고, pity 갱신이 실패해도 요청은 절대 실패하지 않습니다.

---

## 알려진 한계 (원본에서 그대로 물려받음)

**루트 테이블은 전역인데 pity는 프로필별입니다.** 여러 프로필이 붙은 서버에서는 마지막으로
재계산을 유발한 프로필의 확률이 모두에게 적용됩니다. 원본도 세션마다 `tables.locations` 를
통째로 갈아끼우는 방식이라 똑같았습니다. **싱글플레이가 지원 대상입니다.**

---

## 설치

- `PityLoot.dll` + `config/` → `SPT/user/mods/PityLoot/`

## 빌드

```
dotnet build PityLoot.csproj -c Release
```

`$(SptRoot)\SPT\user\mods\PityLoot\` 로 dll + config 를 바로 복사합니다. 기본 `SptRoot` 는
`E:\SPT 4.1`, `-p:SptRoot=...` 로 덮어쓰기, `-p:SkipDeploy=true` 로 복사 생략.

솔루션에는 테스트 프로젝트가 같이 들어 있습니다:

```
dotnet build PityLoot.sln -c Release
dotnet test  PityLoot.sln -c Release
```

원본 TypeScript 소스(`src/`, `types/`, `build.mjs` 등)는 이 커밋에서 지웠습니다. 필요하면
git 히스토리의 `8945ee6` 에 그대로 있습니다.

---

## 확인한 것 / 확인 못 한 것

인게임에서 드랍률이 의도대로 나오는지는 **여기서 확인할 방법이 없습니다.** 틀려도 에러가
안 나고 "체감이 이상함"으로만 나타나기 때문에, 검증 가능한 부분은 최대한 조여뒀습니다.

**pity 계산은 유닛테스트 24개로 원본 TS 동작에 대조했습니다.** "올라가긴 함" 수준이 아니라
숫자를 특정해서 봅니다:

- 스킬 레벨 곡선 (`progress=10` 이 레벨 0인 것까지 — 원본이 `>` 로 도는 것)
- 재고 차감: 같은 스택을 두 과제가 나눠 갖지 못함 / FiR 요구가 먼저 처리됨 /
  비-FiR 요구는 비-FiR 재고부터 씀 / 이미 카운트된 퀘스트 진행도만큼 차감
- 배수: 합산 vs 최대값, 순서 무관, `raid`/`time` 모드가 서로 안 새어나감, 시간은 정수 시간 반올림
- 적용: 위시리스트 우선, 상한 클램프, 열쇠 추가 배수, 반올림(4.5 → 5, JS `Math.round` 와 동일)
- 통화(루블/달러/유로)는 절대 대상에 안 들어감

**DI 의존성은 실제 4.1.5 어셈블리로 확인했습니다.** 컴파일은 타입이 존재하는 것만 증명하지,
SPT 컨테이너가 그 타입을 **등록**하는지는 증명하지 않습니다. 등록되지 않으면 서버가 부팅에
실패합니다:

```
ok    LootTableRewriter(ModConfig, LocationTable, BotTable, ISptLogger`1)
ok    PityService(ModConfig, ProfileHelper, PityTrackerStore, QuestRequirementScanner,
                  HideoutRequirementScanner, LootTableRewriter, TemplateTable, HideoutTable, ISptLogger`1)
ok    PityTrackerStore(ProfileDataService, ISptLogger`1)
ok    ... [Injectable] 11개 전부
static routers: 4
ok    LazyLoad<T>.AddTransformer
ok    LazyLoad<T>.Clear
ALL DEPENDENCIES RESOLVE
```

| | 상태 |
|---|---|
| 실제 4.1.5 어셈블리로 컴파일 | **통과** — 에러 0, 경고 0 |
| pity 계산이 원본 TS와 같은 값을 내는지 | **확인** — 유닛테스트 24개 |
| `[Injectable]` 의존성이 전부 해소되는지 | **확인** — 11개 전부 |
| 라우터 4개가 `StaticRouter` 로 등록되는지 | **확인** |
| `LazyLoad` 변환 훅이 실재하는지 | **확인** |
| 실서버 구동 | **안 함** |
| 인게임 드랍률 | **안 함** |

> 참고: `LocationTable` / `BotTable` / `TemplateTable` / `HideoutTable` 은 `[Injectable]` 이
> 붙은 서비스가 아니라 서버 호스트가 등록하는 데이터 레코드라, 위 검사에서는 "호스트가
> 제공한다"고 가정하고 넘어갑니다. 같은 방식으로 `LocationTable` 을 주입받는 모드가
> 4.1.5에서 실제로 돌고 있는 걸 확인한 뒤에 그렇게 처리했습니다.

## 안 되면 여기부터 보세요

- **아무 변화가 없다**: 서버 로그에 `[PityLoot] active` 가 뜨는지, 그리고
  `N outstanding requirement(s) across M item(s)` 에서 M이 0이 아닌지 보세요. 0이면 필요한
  아이템을 이미 다 갖고 있다고 판단한 겁니다
- **어떤 아이템이 왜 올라갔는지 보고 싶다**: `debug: true`
- **확률 하나하나 다 보고 싶다**: `trace: true` (각오하세요)
- **레이드를 여러 번 뛰었는데 배수가 안 오른다**: `onlyIncreaseOnFailedRaids` 가 기본
  `true` 입니다. 살아서 나오면 카운터가 안 올라갑니다

## License

MIT — 원작자 Bakahashi.
