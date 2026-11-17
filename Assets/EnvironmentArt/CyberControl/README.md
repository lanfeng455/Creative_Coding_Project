# Cyber Control v01

Installed in `Assets/Scenes/TemplateB2.unity` as the independent root `CyberControl_Visuals`.

- Dark navy background, subtle binary streams in two sizes, cyan corner brackets and edge ticks.
- Background and streams stop below `B2_UpperFloor (2)`; the forest and soil are unchanged.
- Cyan contact brackets follow the existing SlingshotBand's visible geometry and letter cooldown state. They do not select, launch or move letters.
- `B1LayerConfig.LetterRecorded` triggers a brief amber dust puff at the soil text row, including when the final letter consumes a complete word. Existing hit sparks remain intact.
- Disable this root to remove the presentation. Input, physics, dinosaur, word matching and item scripts are untouched.

Adjust palette, Columns, Stream Opacity, Stream Speed, Frame Opacity, Selection Feedback and Recording Dust on the root component. No downloaded textures, fonts or 3D models are required.

The three generated meshes are temporary, reused across frames and released on disable. Dust is capped at 192 motes. Visual randomness has a private seed and does not advance gameplay randomness.

Validation: standalone C# compilation against the installed Unity assemblies; scene reference, boundary and original-object preservation checks. Unity shader import, Game View, hand contact, recording feedback and Play/Stop cleanup still require an Editor smoke test; no Unity MCP connection was available during implementation.

Suggested Editor check: open TemplateB2, confirm the background remains below the blue separator; Play and bring the band into contact with a falling letter; record a letter and complete a word; confirm a short amber puff at the text row; Stop/Play again and disable the visual root to check cleanup.
