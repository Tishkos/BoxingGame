# The Boxer

A physics-driven boxing game built in Unity 6000.5.10f1 (URP).

> **New here? Read [impression.md](impression.md)** — what the game is, why it plays the way
> it does, and the three problems that took the real work.

Punches are not animation triggers. Every strike is a physical fist Rigidbody driven
toward a target, layered over an Animancer-blended stance and corrected by IK before
it reaches the renderer. Contact, stagger and recovery fall out of the simulation
rather than being authored per-clip.

## The control scheme

| Input | Action |
| --- | --- |
| Left stick | Movement (always) |
| Right stick | Lean, in any direction, while idle or guarding |
| R1 / L1 | Hold head defense / hold body defense |
| L2 / R2 | Hold to prepare that hand, release to throw |
| L2 / R2 tap | Uncharged fast punch |
| Stick up / down while preparing | Select hook / select uppercut |

Holding a trigger drains stamina past a short grace window and buys a capped power
bonus that fades as the boxer tires. Guard input cancels a prepared punch. Aim
steering is bounded and locks on release.

## Architecture

```
Player Input -> Punch Target -> Physics Controller -> Active Ragdoll -> IK -> Visual Character
```

The rule the whole design hangs on: **never move the fist directly.** No
`hand.position = target.position`, and no IK teleport into the opponent. A physical
fist Rigidbody is asked to reach a desired location and the joints resolve the rest,
so a blocked or mistimed punch behaves correctly without a special case.

Per boxer:

```
BOXER
├── InputController
├── PunchController
├── BalanceController
├── LocomotionController
├── PhysicsBody          (pelvis, chest, head, upper arms, forearms, fists)
├── ConfigurableJoints   (angular limits, drives, springs, damping)
├── Animator / reference pose
├── Full-body biped IK
└── Hit / damage system
```

Animancer owns the stance, three masked punch regions and three matching defense
layers. Procedural correction is applied *after* animation and *before* IK. Punch
clips carry `ExpectedImpact` and attack-window markers, which the timing code reads
instead of guessing strike frames.

## Source layout

| Path | What it is |
| --- | --- |
| `Assets/UndisputedTest/Boxing/` | Current gameplay rig — `AimStudyBoxer`, punch playback, stance, combat, AI |
| `Assets/UndisputedTest/Boxing/Editor/` | Rig build, validation and clip tooling |
| `Assets/Scripts/` | Earlier boxing/physics experiments — health, hitboxes, balance, VFX, audio |
| `Tests/PunchPlaybackChecks.ps1` | Dependency-free timing and buffer checks |
| `docs-design-notes.md` | Design rationale for the physics approach |

## Building this yourself

This repository contains **my own source code only.** The project also depends on
several commercial Unity Asset Store packages which are not redistributable and so are
not included here. Import those into the project and the source here compiles against
them.

For a playable version that needs no purchases, use the compiled build on the
[Releases](../../releases) page.

### Verification

```sh
dotnet build "Assembly-CSharp-Editor.csproj" --verbosity quiet
```

```powershell
& "./Tests/PunchPlaybackChecks.ps1"
```

In Unity: `Tools > Undisputed > Boxing > Validate Selected Aim Study Boxer` checks rig
wiring and clip coverage. Note that compilation and timing tests say nothing about how
the animation *looks* — alternating jabs, both hooks, uppercuts, body shots, recovery
and real bag contact still need checking in Play mode.

## License

My source code is MIT licensed — see [LICENSE](LICENSE). The third-party packages it
depends on are covered by their own licenses and are not included here.
