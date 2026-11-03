# Independent upper screen flow fire

This module ports the supplied five octave rotating gradient flow noise into a Built-in pipeline UI shader. It has its own assembly, material, prefab and runtime entry point. It does not reference or modify the existing gameplay script types.

## Use

Open `Assets/Scenes/TemplateA.unity` and enter Play Mode. `FlowFireBootstrap` creates one independent overlay from `Resources/FocusFlowFire/UpperFireOverlay`. There is no permanent object added to the scene asset.

The effect waits 0.6 seconds, then reveals downward over 8 seconds. Flow continues after the reveal completes. The runtime image is above the existing divider and behind the gameplay overlay, with UI raycasts disabled.

The divider is read from the existing root `Canvas` child `Image`. If it is unavailable, the effect uses a 55 percent screen height boundary. This lookup only reads the existing scene. It never changes that object.

## Adjust

Open `Resources/FocusFlowFire/UpperFireOverlay.prefab` and adjust the `FlowFireReveal` component:

- `Start Delay`: wait before appearance.
- `Reveal Duration`: time to reveal the upper area from top to bottom.
- `Flow Speed`: internal flame turbulence speed.
- `Opacity`: compositing strength.

During Play Mode, use the component context menu `Replay Top Down Flame` to replay the reveal. Each instance has its own temporary material; the source material is never animated or overwritten.

Removing the `Assets/FlowFire` module removes the automatic effect without requiring edits to `TemplateA` or the gameplay scripts.
