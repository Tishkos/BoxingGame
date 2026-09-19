# Third-party notices

This project is built on commercial Unity Asset Store packages. Their licenses do not
permit redistributing their source, so **none of them are included in this repository** —
only my own code is. Each is listed below with its publisher so the dependency graph is
fully auditable.

To build from source, buy and import each package into the Unity project. To simply play
the game, use the compiled build on the [Releases](../../releases) page — the Asset Store
EULA permits these packages to ship inside a compiled application, which is how they are
distributed here.

## Commercial packages

| Package | Publisher | Used for |
| --- | --- | --- |
| Final IK | RootMotion (Pärtel Lang) | Full-body biped IK, Aim IK — the procedural correction layer applied after animation |
| PuppetMaster | RootMotion (Pärtel Lang) | Active ragdoll and physical character response |
| Motion Matching for Unity (MxM) | Kenneth Claassen | Motion-matched locomotion |
| Animancer Pro 8.2.2 | Kybernetik (Elliot Kaiser) | Stance blending, masked punch regions, defense layers |
| Ragdoll Animator 2 | FImpossible Creations | Ragdoll blending and hit reactions |
| MagicaCloth 2 | Magica Soft | Cloth and secondary motion |
| Rewired | Guavaman Enterprises | Gamepad input mapping |
| RadiantGI | Kronnect | Real-time global illumination (URP) |
| UMotion Pro | Soxware Interactive | In-editor animation authoring |
| Toony Colors Pro 2 | Jean Moreno | Character shading |
| Flat Kit | Dustyroom | Stylised environment shading |

## Open source

| Package | Author | License |
| --- | --- | --- |
| KinoBloom | Keijiro Takahashi | MIT |

## Unity packages

All packages in `Packages/manifest.json` are first-party Unity packages (URP, Input
System, Cinemachine, AI Navigation, Timeline, Test Framework and the standard engine
modules), covered by the Unity Companion License / Unity Terms of Service.

## Animation and audio

Animation clips and audio used during development are **not** included in this
repository. Any mocap referenced in the source or in commit history was used as a
local research reference only and is not redistributed here or shipped in any build.

---

If you are a rights holder and believe something in this repository is attributed
incorrectly, please open an issue and I will correct it.
