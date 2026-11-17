# RimWorld 1.6.4871 rev590 검증

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

CE 단독과 두 모드 동시 실행 모두 같은 사수와 표적의 좌표를 고정하고 야간에 다음 값을 측정했다.

| CE 조회값 | 점등 | 소등 | 재점등 |
|---|---:|---:|---:|
| CombatGlowAtFor | 0.5 | 0 | 0.5 |
| GetLightingShift | 0.5 | 1.0 | 0.5 |

이는 CE가 사격 계산에 사용하는 어둠 페널티 측정값이다. 실탄 명중 비율을 측정한 값은 아니다. 깨끗한 Git 내보내기에서 다시 빌드한 DLL의 SHA-256은 동봉 파일과 같은 `24756D313EAF64E8926B2C7A44BFC8072B53396310C4B8F35A0561FAD12143C5`였다.

## 실행 방법

Windows PowerShell에서 mod 루트를 기준으로 실행한다. 첫 두 테스트는 게임을 실행하지 않는다. 게임 테스트는 설치된 게임 실행 파일을 사용하며 각 실행마다 임시 프로필을 만들고 그 경로를 출력한다.

```powershell
.\build.ps1
.\Tests\VerifyPackage.ps1
$geometry = Join-Path $env:TEMP 'DirectedSearchlights-geometry'
dotnet build .\Tests\Geometry\GeometryTests.csproj -c Release -o $geometry
dotnet (Join-Path $geometry 'GeometryTests.dll')
.\Tests\Scenarios\Run-Scenarios.ps1
.\Tests\Runtime\Run-Runtime.ps1 -Case vanilla
.\Tests\Runtime\Run-Runtime.ps1 -Case ce
.\Tests\Runtime\Run-Runtime.ps1 -Case ao -UseVanillaWeather
.\Tests\Runtime\Run-Runtime.ps1 -Case both -UseVanillaWeather
```

시나리오의 `scenario-results.txt`와 통합 테스트의 `runtime-results.txt`, `Player.log`는 해당 실행의 임시 프로필에 남는다. 일반 플레이에서 재현하려면 밤에 60칸 앞을 Fixed로 지정하고 중간에 문을 둔다. 문을 닫으면 빔이 문 앞에서 잘리고, 열면 끝점 광원이 원래 위치로 돌아가야 한다. CE의 효과를 비교할 때는 같은 사수와 표적, 거리, 날씨를 유지하고 다른 조명과 총구 섬광을 배제한다.
