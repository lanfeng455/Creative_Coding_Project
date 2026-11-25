// ObjectCapabilityIds.cs —— Prototype C：物件能力库的能力 ID 目录（v0.1）
// 能力分两类：OUTPUT = 物件对外产生的影响；INPUT = 物件接收外部影响后的状态变化。
// 这里是「已注册能力 ID」的唯一事实来源：
//   - LLM Prompt 的能力目录文案从这里生成（DescribeCatalog）；
//   - Schema 校验（ObjectDefinitionValidator）从这里做宽松匹配；
//   - CapabilityAssembler 按规范 ID 挂组件。
// LLM 只能从这里选 ID，不允许输出任意组件名 / 脚本名 / 代码。

using System;
using System.Text;

namespace PrototypeC
{
    public enum ObjectCapabilityKind
    {
        Input,
        Output,
    }

    public static class ObjectCapabilityIds
    {
        // —— OUTPUT：对外产生影响 ——
        public const string EmitAirflow = "emit_airflow";
        public const string EmitLight = "emit_light";
        public const string EmitSound = "emit_sound";

        // —— INPUT：接收外部影响 ——
        public const string RespondToAirflow = "respond_to_airflow";
        public const string RespondToLight = "respond_to_light";
        public const string RespondToSound = "respond_to_sound";

        /// <summary>
        /// INPUT：被推动（位移，并可能由此改变朝向 / 照射方向 / 风向）。
        /// 由现有行为装配的 Dynamic 刚体 + GeneratedObjectPushRelay 提供——
        /// 能力装配器只认这个 ID、不另挂组件，避免重复实现推动机制。
        /// </summary>
        public const string ReceiveForce = "receive_force";

        /// <summary>全部已注册能力 ID（Prompt 与校验都用这一份）。</summary>
        public static readonly string[] All =
        {
            EmitAirflow, EmitLight, EmitSound,
            RespondToAirflow, RespondToLight, RespondToSound, ReceiveForce,
        };

        public static ObjectCapabilityKind KindOf(string id)
        {
            return id == EmitAirflow || id == EmitLight || id == EmitSound
                ? ObjectCapabilityKind.Output
                : ObjectCapabilityKind.Input;
        }

        /// <summary>宽松匹配（大小写 / 空白不敏感）→ 规范 ID；未注册返回 false。</summary>
        public static bool TryNormalize(string raw, out string id)
        {
            id = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string key = raw.Trim();
            foreach (string known in All)
            {
                if (string.Equals(known, key, StringComparison.OrdinalIgnoreCase))
                {
                    id = known;
                    return true;
                }
            }

            return false;
        }

        /// <summary>给 LLM System Prompt 用的能力目录（两行：OUTPUT / INPUT）。</summary>
        public static string DescribeCatalog()
        {
            var sb = new StringBuilder();
            sb.Append("outputs (this object affects others): ")
              .Append(EmitAirflow).Append(", ").Append(EmitLight).Append(", ").Append(EmitSound);
            sb.Append("; inputs (this object reacts to others): ")
              .Append(RespondToAirflow).Append(", ").Append(RespondToLight).Append(", ")
              .Append(RespondToSound).Append(", ").Append(ReceiveForce);
            return sb.ToString();
        }
    }
}
