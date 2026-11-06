# Independent upper screen flow fire

This module ports the supplied five octave rotating gradient flow noise into a Built-in pipeline UI shader. It has its own assembly, material, prefab and runtime entry point. It does not reference or modify the existing gameplay script types.

## Use

Open `Assets/Scenes/TemplateA.unity` and enter Play Mode. `FlowFireBootstrap` creates one independent overlay from `Resources/FocusFlowFire/UpperFireOverlay`. There is no permanent object added to the scene asset.

The effect waits 0.6 seconds, then the flame sweeps in from the upper right corner toward the 45 degree line. Flow continues after the reveal completes. The runtime image is above the existing divider and behind the gameplay overlay, with UI raycasts disabled.

The divider is read from the existing root `Canvas` child `Image`. If it is unavailable, the effect uses a 55 percent screen height boundary. This lookup only reads the existing scene. It never changes that object.

The flame is clipped to the right side of the 45 degree split line authored by the gameplay bar. `FlowFireReveal` reads the two runtime markers `Canvas/UpperSplitRig/UpperSplitStart` and `UpperSplitEnd` and passes them to the shader in the image's local space, so the flame never covers the typewriter on the left. The shader projects a reveal front parallel to the split line: `_Reveal` 0 keeps it at the upper right corner, `_Reveal` 1 pushes it onto the line. If the markers are absent, the flame fills the whole upper area from top to bottom.

## Adjust

Open `Resources/FocusFlowFire/UpperFireOverlay.prefab` and adjust the `FlowFireReveal` component:

- `Start Delay`: wait before appearance.
- `Reveal Duration`: time to push the flame front from the upper right corner to the split line.
- `Flow Speed`: internal flame turbulence speed.
- `Opacity`: compositing strength.

During Play Mode, use the component context menu `Replay Diagonal Flame` to replay the reveal. Each instance has its own temporary material; the source material is never animated or overwritten.

Removing the `Assets/FlowFire` module removes the automatic effect without requiring edits to `TemplateA` or the gameplay scripts.
