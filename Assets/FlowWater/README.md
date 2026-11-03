# Independent reflective wave preview

This preview retains the supplied water shader's rotating five octave height field and reflected light, and adds a separate two dimensional travelling wave silhouette. It uses the Built-in render pipeline and its own assembly, materials, prefab and entry point.

## Preview

Enter Play Mode in `Assets/Scenes/TemplateA.unity`. The module creates a runtime overlay above the existing fire and below gameplay UI, then waits with water hidden. It reads the existing divider position and clips the water to the upper area. The overlay is removed automatically when Play Mode exits; the scene asset is unchanged.

## WATER encounter

The independent `WaterWordEncounter` observes direct `Fruit_*` children of the existing `Canvas/AlphabetBar`. It uses sibling order, which is the existing fruit generation order, and recomputes from surviving fruit objects after erasure. Only the latest five displayed letters spelling WATER in that order trigger the effect. Older letters can remain, but an extra letter within the latest five prevents a match until corrected. A 0.2 second stable match starts one sweep. No existing letter, rain or hand tracking script is edited or referenced by type.

The fire now reveals downward over 45 seconds, giving the player time to compose a word. It remains available until WATER is completed; there is no spelling timeout.

After WATER, the water advances left to right using the prefab's `Sweep Duration` (currently 10 seconds). At full coverage, the runtime fire object is disabled. The water holds for `Hold Duration` (2 seconds), then clears completely. The encounter remains complete with neither effect visible and cannot retrigger from the same or additional letters. Starting Play Mode again starts a fresh encounter. Existing letters are left intact.

The shaders, water texture, original fire scripts and original scene asset remain unchanged. Only the independent runtime controller coordinates display state; the fire prefab reveal timing and water prefab hold timing are the relevant asset parameter changes.

For a camera independent art preview, open `Scenes/FlowWaterPreview.unity` in this folder and press Play. This scene keeps the automatic preview loop and has no MediaPipe or gameplay dependencies. Its outline is a single continuously undulating surface with a gently tapered moving front; there is no separate curling crest.

## Adjust

Edit `Resources/FocusFlowWater/UpperWaterPreview.prefab` to adjust sweep and hold timing, opacity, ripple speed, render scale, maximum render texture height and surface frame rate. Start Delay, Auto Loop, Fade Duration and Loop Pause apply only to the standalone art preview. In TemplateA, WATER triggers immediately and the effects clear exactly after the configured hold.

Edit `Materials/ReflectiveWaterSurface.mat` for deep water, body and reflection colors. Edit `Materials/WaterWaveSilhouette.mat` for the wave edge color and edge softness. Runtime animation uses temporary material instances and a reusable RenderTexture, not writes to these source assets.

`SetInspectionFrame(progress, waterTime)` holds an art inspection sample. `RestartPreview()` replays the standalone art loop; it cannot restart a completed TemplateA encounter. Normal gameplay uses `WaitForWord()` and `PlayForCompletedWord()` instead of the art loop.

Remove this folder to remove the water preview. No original scene or gameplay source needs to be edited.

## Source

Water height and reflective surface adapted from the user supplied Stockholms Strom shader by Peder Norrby / Trapcode, 2016, CC BY-NC-SA 3.0. Simplex noise adapted from Ian McEwan / Ashima Arts, MIT, https://github.com/ashima/webgl-noise. The new wave silhouette, Unity UI adaptation and preview control are additions for this project.
