# Directed Searchlights 검증

## 2026-11-18: 둥근 빔 끝과 1.7 빌드

### 스마트 앱 컨트롤 해제 후 재시험

사용자가 차단을 해제한 뒤 같은 테스트를 다시 실행했다. 아래의 초기 차단 기록은 이 재시험으로 해소됐다.

- `dotnet run --project Tests/Geometry/GeometryTests.csproj -c Release`: `Geometry checks passed.` 정상 종료.
- `.\Tests\Visual\Run-Visual.ps1`: RimWorld 1.7.5011에서 `PASS`, 정상 종료. 테스트 하네스 빌드도 경고/오류 0.
- 야간 `skyGlow=0`, 18칸·60칸 표적 셀의 `GroundGlowAt(ignoreSky: true)=0.5`.
- 60칸 조준 중 30칸에 벽을 세우면 광원 위치가 29칸으로 이동했다. 캡처에서 빔이 벽 앞에서 잘리고 벽 뒤로 이어지지 않는 것을 확인했다.
- 60칸 끝점 확대 화면에서 둥근 끝과 부드러운 감쇠를 확인했다. [끝점 화면](rounded-tip-1.7.jpg), [벽 차폐 화면](rounded-wall-1.7.jpg), [수치 기록](visual-results-1.7.txt).
- 별도 프로필: `%TEMP%\SearchlightVisual-f04020603ca941a49f77bd979a6309c0`. 원본 PNG는 이 폴더에 있으며 문서에는 축소 JPEG를 보관했다.
- 이 재시험은 Core + 탐조등의 고정 조준 및 렌더링 시험이다. CE/AO 조합, 이동 표적, 저장 복원, 전력망 운용 전체를 1.7에서 다시 검증한 것은 아니다.

### 초기 빌드 및 차단 기록

현재 설치본은 RimWorld 1.7.5011 rev24다. 사용자 요청에 따라 동일한 C# 소스를 새 설치본의 호환 어셈블리로 빌드했다. `build.ps1`은 1.6/1.7을 감지하고 각각 별도 DLL을 출력하며, 1.7의 Def는 1.6 원본에서 복사한다.

- `.\build.ps1`: 1.7 빌드 성공, 경고/오류 0.
- `.\Tests\VerifyPackage.ps1 -GameVersion 1.7`: 통과. 기존 1.6 패키지도 검사 통과.
- `dotnet build Tests/Geometry/GeometryTests.csproj -c Release --no-restore`: 성공, 경고/오류 0. 새 테스트는 곡선 형태, 조준점 밝기, 외곽 투명도, 60칸 뒤의 벽 차폐를 확인한다.
- 기하 테스트 실행은 Windows 애플리케이션 제어(0x800711C7)에 차단되어 통과로 기록하지 않는다.
- `.\Tests\Visual\Run-Visual.ps1`: 테스트 모드는 경고/오류 없이 빌드됐지만, RimWorld 자체의 `hostfxr.dll` 로딩이 Windows 애플리케이션 제어(0x800711C7)에 차단되어 게임 시작 전에 종료됐다. 따라서 1.7 화면, 둥근 메시, 런타임 차폐는 아직 실측하지 못했다. 재실행용 하네스는 18칸, 60칸, 끝점 확대, 중간 벽 차폐 장면을 별도 프로필에서 캡처하도록 준비했다. 전력 상태를 강제하는 시각 테스트이며 전력망 통합 테스트가 아니다.
- 실패 로그 예: `%TEMP%\SearchlightVisual-b72f16e8d27a46bab135a7b6b9493289\console-error.log`. 스크린샷은 생성되지 않았다. 운영체제 정책을 변경하거나 우회하지 않았다.
- 현재 1.6 설치본이 없어 동봉된 1.6 DLL은 이전 버전이다. 아래 1.6 측정 결과는 이전 테스트 기록이며 새 빔 끝이나 1.7의 검증 결과로 해석하지 않는다.

전력은 150W/200W로 유지했다. 근거는 [전력 비교 기록](power-balance.md)을 참고한다.

## 이전 RimWorld 1.6.4871 rev590 검증

실제 게임을 별도 세이브 경로에서 실행했다. 테스트가 생성한 발전기와 전력망을 사용하는 통합 테스트와, 문·지붕·사거리 판정을 재현하기 위해 전력 상태를 고정하는 시나리오 테스트를 함께 사용했다.

| 관찰 항목 | 게임에서 확인한 동작 |
|---|---|
| Fixed / Sweep / Automatic | 적이 있어도 Fixed의 탐색 횟수는 0이었다. 빈 부채꼴에서 Sweep는 회전했고 Automatic은 탐색 방향을 계속 바꿨다. |
| 60칸 경계 | 정확히 60칸에서 표적 잠금, 조준 거리, 표적 셀의 광점을 확인했다. 61칸에서는 잠금을 해제했다. |
| 벽과 문 | 벽 너머의 실외 Pawn에 대해 터렛은 잠금을 해제하고 타워는 유지했다. 타워의 광점은 벽 앞에 남았다. 문을 열자 터렛이 표적을 다시 포착했다. |
| 지붕 | 지붕 아래 표적은 타워의 탐지 대상에서 제외됐다. 타워 자체에 지붕을 씌우자 운용이 정지하고 광점이 사라졌다. |
| 야간 실제 조도 | 바닐라에서 이동하는 적을 추적하고 표적과 광점의 셀에서 각각 `GroundGlowAt(ignoreSky: true) = 0.5`를 읽었다. |
| 종료와 복구 | 스위치를 끄자 광점이 사라졌다. 전력 복구 후 다시 나타났다. 건물을 철거하면 해당 광점이 제거됐다. 발전기가 연결된 10개를 저장하고 같은 프로세스에서 불러와 건물 수, 전력, 잠금과 광점을 확인했다. |

한 바닐라 실행에서 10개가 동시에 켜졌을 때 광점은 10개였고 게임 360틱에 998ms가 걸렸다. 별도의 180틱 실험은 0개 487ms, 고정식 10개 595ms였다. 이 수치는 시간 배속과 프레임 스케줄링을 포함한 벽시계 측정이며, 램프 한 개의 CPU 비용이나 대형 식민지의 프레임률을 나타내지는 않는다.

## CE와 Atmosphere Overhaul

RimWorld 1.6.4871에서 바닐라, CE 16.7.4.0, Atmosphere Overhaul 단독, CE와 Atmosphere Overhaul 동시 활성 조합이 전력·조명·저장 복원 테스트를 통과했다. Atmosphere Overhaul 통과 기록은 자체 설정의 VanillaDerivation 날씨 엔진을 사용했다. 기본 Simulator 엔진에서는 테스트가 시각을 밤으로 건너뛴 뒤 시간이 일시정지 상태에 머물렀고, 그 구성의 전체 동작은 확인하지 못했다.

이전 반경 6칸 버전에서 CE 단독과 두 모드 동시 실행 모두 같은 사수와 표적의 좌표를 고정하고 야간에 다음 값을 측정했다.

| CE 조회값 | 점등 | 소등 | 재점등 |
|---|---:|---:|---:|
| CombatGlowAtFor | 0.5 | 0 | 0.5 |
| GetLightingShift | 0.5 | 1.0 | 0.5 |

이는 CE가 사격 계산에 사용하는 어둠 페널티 측정값이다. 실탄 명중 비율을 측정한 값은 아니다. 이전 반경 6칸 버전의 깨끗한 Git 내보내기에서 다시 빌드한 DLL의 SHA-256은 `24756D313EAF64E8926B2C7A44BFC8072B53396310C4B8F35A0561FAD12143C5`였다.

## 자동 줌과 연동한 끝점 광원

최신 빌드는 기본 지름 4칸에 대해 물리적 반경 2칸을 사용한다. RimWorld 1.6의 광원 탐색은 중심 셀의 거리를 1부터 세므로 `CompGlower.GlowRadius`에는 1을 더한다. 무보정 반경 2를 시도했을 때는 인접 셀 조도가 0이었고, 아래는 이 보정을 적용한 최종 빌드의 측정값이다.

탐조등 하나를 15칸 앞에 고정하고, 현지 시각 1시와 하늘 조도 0에서 측정했다. 같은 사수에게 CE 고급 야간투시경을 실제로 착용시켜 전후를 비교했다. 대각 한 칸은 좌표 차이 (1, 1)이다.

| 광점 기준 위치 | 지면 조도 | CE 전투 조도 | 맨눈 페널티 | 고급 야간투시경 페널티 |
|---|---:|---:|---:|---:|
| 중심 | 0.5 | 0.500 | 0.500 | 0.200 |
| 축 방향 한 칸 | 0.5 | 0.355 | 0.645 | 0.258 |
| 대각 한 칸 | 0.5 | 0.250 | 0.750 | 0.300 |
| 축 방향 두 칸 | 0 | 0.145 | 0.855 | 0.342 |
| 축 방향 세 칸 | 0 | 0 | 1.000 | 0.400 |

CE 16.7.4.0은 표적 셀과 주변 8셀의 조도를 가중평균하고 밤에는 0.5로 제한한다. 사수의 최종 야간시야 효율을 N, 전투 조도를 G라 하면 어둠 페널티는 max((1−G)(1−N), 0)이다. 실제 명중 확률은 이 수치와 별개다. 기본 등급 단안경의 효율은 0.4, 고급 고글은 0.6이다.

AO 기본 Simulator의 자연 시간 재시험은 맵 생성까지 진행했지만 420초 제한 내에 조명 시험이 시작되지 않았다. 초기 예보 준비 자체에 최대 600초를 허용하는 모드이므로 이 결과만으로 비호환이라고 판단할 수 없다. 더 긴 기본 엔진 재현은 PowerShell에서 `.\Tests\Runtime\Run-Runtime.ps1 -Case ao -TimeoutSeconds 1200`으로 실행할 수 있다. 별도 기하 테스트 실행 파일은 이번 실행에서 Windows 애플리케이션 제어가 차단했다. 실제 게임 시나리오의 60칸 광원 반경·벽 차폐 검사와 CE 광원 테스트는 통과했다.

## 실행 방법

Windows PowerShell에서 mod 루트를 기준으로 실행한다. 첫 두 테스트는 게임을 실행하지 않는다. 게임 테스트는 설치된 게임 실행 파일을 사용하며 각 실행마다 임시 프로필을 만들고 그 경로를 출력한다.

```powershell
.\build.ps1
.\Tests\VerifyPackage.ps1
$geometry = Join-Path $env:TEMP 'DirectedSearchlights-geometry'
dotnet build .\Tests\Geometry\GeometryTests.csproj -c Release -o $geometry
dotnet (Join-Path $geometry 'GeometryTests.dll')
.\Tests\Scenarios\Run-Scenarios.ps1
.\Tests\Optics\Run-Optics.ps1
.\Tests\Runtime\Run-Runtime.ps1 -Case vanilla
.\Tests\Runtime\Run-Runtime.ps1 -Case ce
.\Tests\Runtime\Run-Runtime.ps1 -Case ao -UseVanillaWeather
.\Tests\Runtime\Run-Runtime.ps1 -Case both -UseVanillaWeather
```

시나리오의 `scenario-results.txt`와 통합 테스트의 `runtime-results.txt`, `Player.log`는 해당 실행의 임시 프로필에 남는다. 일반 플레이에서 재현하려면 밤에 60칸 앞을 Fixed로 지정하고 중간에 문을 둔다. 문을 닫으면 빔이 문 앞에서 잘리고, 열면 끝점 광원이 원래 위치로 돌아가야 한다. CE의 효과를 비교할 때는 같은 사수와 표적, 거리, 날씨를 유지하고 다른 조명과 총구 섬광을 배제한다.
