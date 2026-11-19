// ToonMaterialGenerator.cs —— Prototype C：glTFast 材质生成器的 toon 装饰器
// 做法：包住 glTFast 默认材质生成器，生成出 URP 材质后立刻替换成 PrototypeC/Toon，
// 并搬运 albedo 贴图 / 基础色 / alpha clip。这样运行时加载的 GLB（AI 生成模型）
// 自动带卡通化材质，不需要用户事后手动替换。

using GLTFast;
using GLTFast.Logging;
using GLTFast.Materials;
using GLTFast.Schema;
using UnityEngine;

namespace PrototypeC
{
    public class ToonMaterialGenerator : IMaterialGenerator
    {
        private readonly IMaterialGenerator _inner;
        private readonly ToonLayer _layer;

        public ToonMaterialGenerator(ToonLayer layer)
        {
            _layer = layer;
            _inner = MaterialGenerator.GetDefaultMaterialGenerator();
        }

        public static ToonMaterialGenerator Create(ToonLayer layer = ToonLayer.Foreground)
        {
            return new ToonMaterialGenerator(layer);
        }

        public UnityEngine.Material GetDefaultMaterial(bool pointsSupport = false)
        {
            // 默认材质由 glTFast 内部缓存复用，不能就地改 shader，这里只能克隆。
            return ToonMaterials.Create(_inner.GetDefaultMaterial(pointsSupport), _layer);
        }

        public UnityEngine.Material GenerateMaterial(MaterialBase gltfMaterial, IGltfReadable gltf, bool pointsSupport = false)
        {
            // 刚生成、没有其它引用，直接改 shader，避免多克隆一份材质。
            return ToonMaterials.SwitchInPlace(_inner.GenerateMaterial(gltfMaterial, gltf, pointsSupport), _layer);
        }

        public void SetLogger(ICodeLogger logger)
        {
            _inner.SetLogger(logger);
        }
    }
}
