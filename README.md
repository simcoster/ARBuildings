# ARBuildings

Unity AR app for placing and previewing buildings in the real world using ARCore Geospatial —
streetscape geometry, solar-position-driven lighting, and real-time shadow casting.

## Requirements

- Unity 6 (see `ProjectSettings/ProjectVersion.txt` for the exact version)
- ARCore Extensions 1.54.0
- An ARCore-supported Android device, or an ARKit-supported iOS device

## Setup

API keys are not committed. After cloning:

1. Copy the template:

   ```
   cp ProjectSettings/ARCoreExtensionsProjectSettings.json.template ProjectSettings/ARCoreExtensionsProjectSettings.json
   ```

2. Fill in `AndroidCloudServicesApiKey` and `IOSCloudServicesApiKey` with your own Google Cloud
   API keys that have the **ARCore API** enabled.

   You can also set these in the Unity Editor under
   *Edit → Project Settings → XR Plug-in Management → ARCore Extensions*.

3. Open the project in Unity and load `Assets/Scenes/SampleScene.unity`.

## Layout

| Path | Contents |
| --- | --- |
| `Assets/Scripting/` | Geospatial controller, building placement/loading, solar position, lighting |
| `Assets/Shaders/` | Ghost wireframe shader for placement preview |
| `Assets/Scenes/` | Main scene |
| `Assets/XR/` | ARCore / ARKit loader and runtime settings |
