# Prototype B2 Fire and Water VFX

Open `Assets/Scenes/TemplateB2.unity` and enter Play Mode. Choose **Tools → FOCUS → B2 → Preview Fire and Water VFX** to see the full sequence without waiting for the fire timer or performing a hand gesture. The preview launches test water from below the upper floor.

For the regular input path, assemble `WATER` during a fire. The current typewriter spawn position is already above the upper floor, so the dinosaur receives the item there. Water that starts below the upper floor keeps the original slingshot physics until it reaches the upper layer.

The package loads only in `TemplateB2` at runtime. It uses the scene's existing fire scheduler, dinosaur, typewriter, and slingshot. Removing this folder restores the original behavior. The three `strict/grid` textures are from PVFX Foundry 1.0 and covered by the bundled license. The steam, droplet trail, and hiss are generated at runtime.
