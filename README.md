# Directed Searchlights

RimWorld 1.6용 지향성 탐조등입니다. 터렛과 타워는 최대 60칸 안에서 적을 추적합니다. 기본 설정으로 60칸 앞을 겨누면 빔의 전체 각도는 약 3.8°이고, 그 거리에서의 지름은 4칸입니다.

## 설치와 조작

이 폴더는 모드 루트입니다. RimWorld의 모드 목록에서 **Directed Searchlights**를 활성화하고 재시작하세요. 빌드된 DLL이 동봉됩니다. 전기를 연구한 다음 보안 탭에서 터렛(150W, 강철 50, 부품 1) 또는 타워(200W, 강철 75, 부품 1)를 건설하고 전력망에 연결하세요. 터렛은 지붕 아래에서도 작동합니다. 타워 위에 지붕이 생기면 정지합니다.

| 모드 | 동작 |
|---|---|
| Fixed | 지정한 셀의 방향과 거리로 고정합니다. |
| Sweep | 지정 방향을 중심으로 좌우 45°를 왕복합니다. 적을 놓치면 순찰로 돌아갑니다. |
| Automatic | 360°를 순찰하다가 유효한 적을 추적합니다. |

건물을 선택한 뒤 기준 셀을 지정해 방향과 순찰 거리를 정하세요. Sweep와 Automatic에서는 적을 직접 지정할 수도 있습니다. 타워는 벽 뒤의 실외 적을 탐지하지만, 빔은 터렛과 마찬가지로 벽이나 닫힌 문에서 잘립니다.

## 빔과 게임 조도

모드 설정에서 표적 위치의 빔 지름을 2~6칸으로 조절합니다. 빔 전체 각도는 현재 조준 거리 `d`에 대해 `min(24°, 2 × atan(지름 / (2d)))`입니다. 장애물이 중간에서 빔을 가로막아도 줌은 조준 거리에 맞춰집니다. 기본 지름 4칸으로 60칸 앞을 겨누면 30칸 지점의 빔 폭은 약 2칸입니다.

빔은 화면에 표시하고, 실제 조도는 도달한 조사 지점에 바닐라 광원을 놓아 제공합니다. 광원 반경은 그 지점의 빔 폭의 절반입니다. 기본 빔이 표적에 도달하면 반경 2칸이고, 60칸 조준 중 30칸에서 가로막히면 반경 약 1칸입니다. 바로 앞에서 잘릴 때는 셀 중심을 밝히도록 최소 반경 0.5칸을 사용합니다. 따라서 빔 중간의 셀은 빔만으로 밝아지지 않고, 끝점 근처에서는 빔 폭 바깥의 셀도 밝아질 수 있습니다. CE는 바닐라 조도와 사수의 야간시야 능력을 함께 사용합니다. CE 16.7.4.0에서 야간 전투 조도 상한은 0.5입니다.

## 빌드

Windows에 .NET SDK와 .NET Framework 4.7.2 targeting pack을 설치한 뒤 PowerShell에서 실행하세요. 개발에 사용한 게임 버전은 1.6.4871 rev590, SDK는 10.0.404입니다.

```powershell
.\build.ps1
# 게임 설치 폴더 밖에서 빌드할 때
.\build.ps1 -RimWorldPath 'D:\SteamLibrary\steamapps\common\RimWorld'
```

출력 파일은 `1.6/Assemblies/DirectedSearchlights.dll`입니다. 게임 어셈블리는 설치본을 참조하며 배포물에는 포함하지 않습니다.

## 원작과 참고 자료

운용 모드와 끝점 광원의 아이디어는 Rikiki의 MiningCo. Projector에서 참고했습니다. 독립 Def, 코드, 그래픽을 사용합니다. [Rikiki의 Ludeon 배포 스레드](https://ludeon.com/forums/index.php?topic=14711.msg153946#msg153946) · [MiningCo. 원작 소스](https://github.com/Rikiki123456789/Rimworld/tree/master/MiningCo.%20Projector) · [Zaljerem의 1.6 이식판](https://steamcommunity.com/sharedfiles/filedetails/?id=3712705276)

CE의 표적 조도와 사격 페널티를 확인한 코드입니다. [LightingTracker](https://github.com/CombatExtended-Continued/CombatExtended/blob/d847807b84ab7c140f89390c3032e3947b9ab386/Source/CombatExtended/CombatExtended/LightingTracker.cs) · [Verb_LaunchProjectileCE](https://github.com/CombatExtended-Continued/CombatExtended/blob/d847807b84ab7c140f89390c3032e3947b9ab386/Source/CombatExtended/CombatExtended/Verbs/Verb_LaunchProjectileCE.cs)

## 검증과 재현

[RimWorld 1.6 검증 기록](docs/verification/README.md)에서 게임 테스트 결과와 실행 명령을 확인할 수 있습니다. 다음은 별도 테스트 맵에서 찍은 야간 추적 장면입니다.

![이동하는 Pawn을 비추는 두 탐조등](docs/verification/night-tracking.jpg)
