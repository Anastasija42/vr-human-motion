# Unity project — assets not committed

`unity/KimodoUnity/` is the Unity project (Unity 6000.5, URP) without the
large third-party art packs, which exceed GitHub's file-size limits. Re-import
them from the Asset Store / their sources into `Assets/` before opening the
`lab.unity` scene, or the furniture will show as missing prefabs:

- **Furniture Mega Pack** (`Assets/Furniture Mega Pack`) — tables, drawers, cabinets used in the lab scene
- **TextureHaven** floor/wall textures (`Assets/TextureHaven`)
- **QA_Books**, **Phoenix3D**, **Office Supplies Low Poly**, `Assets/textures`
- The alternative Mixamo character `Ch08_nonPBR (1).fbx` (the committed `character.fbx` is enough)

Everything the pipeline itself needs is committed: `Assets/KimodoVR/` (our
scripts + controller guide), `Assets/URDF/` (Franka Panda), `Assets/Scripts/`,
`Packages/` (bridge package + manifest), `ProjectSettings/` (OpenXR profiles,
URP), and the scenes.
