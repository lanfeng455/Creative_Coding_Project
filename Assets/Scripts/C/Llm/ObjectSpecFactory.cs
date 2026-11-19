// ObjectSpecFactory.cs —— Prototype C：把校验通过的 ObjectDefinition 组装成 ObjectSpec
// 只搬「LLM 能决定的东西」：objectName + 两段描述 + behavior/placement/size；
// 生成参数、朝向、期望尺寸、锚点局部坐标等 Unity 管理的字段一律由调用方传入。

using UnityEngine;

namespace PrototypeC
{
    public static class ObjectSpecFactory
    {
        public static ObjectSpec Create(
            ObjectDefinition definition,
            PromptStyle style,
            GenerationParams generation,
            float yawDegrees,
            Vector3 expectedSize,
            Vector3 placement)
        {
            var spec = new ObjectSpec
            {
                objectName = definition.objectName,
                objectDescription = definition.objectDescription,
                sizeDescription = definition.sizeDescription,
                style = style ?? new PromptStyle(),
                behavior = definition.behavior.ToString(),
                placementKind = definition.placement.ToString(),
                sizeClass = definition.size.ToString(),
                placement = placement,
                yawDegrees = yawDegrees,
                size = expectedSize,
                generation = generation != null ? generation.Clone() : new GenerationParams(),
            };

            // Prompt 由「风格层 + 两段描述」拼装，风格模板不动，缓存指纹随之保持不变
            spec.ComposePrompt();
            return spec;
        }
    }
}
