// RoomOccupancy.cs —— Prototype C：已落位物件的占用登记 + 容量淘汰 + 布局落盘
// 职责边界：
//   ① 记录「谁占了哪段横向位置」（锚点局部 X + 实际宽度），供 LayoutSolver 做区间相交检测；
//   ② 全屋容量上限（默认 10 个，不按分区计），超了按 FIFO 淘汰最早放入的那个；
//   ③ 把登记表写进 persistentDataPath/ObjectCache/layout.json，重进 Play 可回放。
// 淘汰只销毁场景实例并释放登记，绝不删除模型缓存（persistentDataPath 与 PrototypeC_Cache 都保留）。
// 这里的 FIFO 是「已落位物件的淘汰顺序」，与 PrototypeCTestUI 的生成队列（待处理请求）是两套状态，互不影响。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PrototypeC
{
    [Serializable]
    public class PlacedItem
    {
        [Tooltip("规范化物件标识（与模型缓存 key 一致）")]
        public string id = string.Empty;

        public string objectName = string.Empty;

        public RoomBand band = RoomBand.Ground;

        [Tooltip("锚点局部坐标的包围盒中心（米）")]
        public Vector3 localCenter = Vector3.zero;

        [Tooltip("实际包围盒尺寸（米，缩放后）")]
        public Vector3 size = Vector3.zero;

        public float yawDegrees = 0f;

        [Tooltip("Touch 可推物件：每帧按当前 transform 刷新占用位置")]
        public bool dynamic = false;

        [Tooltip("加入时间（本地时间字符串，仅用于日志与 FIFO 说明）")]
        public string addedAt = string.Empty;

        [Tooltip("行为标签（Touch / Rest / Play / Ignore），回放时用来恢复物理")]
        public string behavior = string.Empty;

        [Tooltip("尺寸档位（Small / Medium / Large / SuperLarge），回放时用来恢复缩放")]
        public string sizeClass = string.Empty;

        [Tooltip("LLM 给出的布局意图；回放时按当前占位重新求解，不复用旧坐标")]
        public LayoutIntent intent;

        [NonSerialized] public GameObject instance;
    }

    [DisallowMultipleComponent]
    public class RoomOccupancy : MonoBehaviour
    {
        // Inspector 字段按「配置动线」排序：先核对 → 调参 → 存档。
        // Unity 按字段名序列化，调换顺序不会丢已配好的值。

        [Header("① 先核对这几项")]
        [Tooltip("房间几何描述；留空自动从场景里找 RoomLayout")]
        public RoomLayout layout;

        [Tooltip("坐标基准（锚点局部坐标）；留空自动取 RoomLayout.anchor")]
        public Transform anchor;

        [Tooltip("全屋最多同时放几个物件（不分层，三层共用这个名额）；放第 N+1 个时按「先放先淘汰」销毁最早的那个，模型缓存保留")]
        public int maxObjects = 10;

        [Header("② 摆放规则（一般不用改）")]
        [Tooltip("物件之间保留的安全间距（米）；仅在没有 RoomLayout 时兜底，有 RoomLayout 时统一用 RoomLayout.clearance，保证与 LLM 提示词一致")]
        public float overlapMargin = 0.15f;

        [Header("③ 存档 layout.json（一般不用改）")]
        [Tooltip("把登记表写进 persistentDataPath/ObjectCache/layout.json；关掉则本次运行不落盘、也不回放")]
        public bool persist = true;

        [Tooltip("进入 Play 时读取 layout.json（只恢复占用，不恢复模型实例）")]
        public bool loadOnStart = true;

        [Tooltip("布局文件名，放在 ObjectCache 根目录下；不要放进某个物件的缓存目录")]
        public string fileName = "layout.json";

        /// <summary>有物件被容量淘汰时触发（供 UI 提示）。</summary>
        public event Action<PlacedItem> Evicted;

        private readonly List<PlacedItem> _items = new List<PlacedItem>();

        public IReadOnlyList<PlacedItem> Items => _items;
        public int Count => _items.Count;
        public int Capacity => Mathf.Max(1, maxObjects);

        public string FilePath =>
            Path.Combine(ObjectCache.RootPath, string.IsNullOrWhiteSpace(fileName) ? "layout.json" : fileName);

        private void Awake()
        {
            ResolveReferences();
            if (loadOnStart) Load();
        }

        /// <summary>补齐引用（只填空，不覆盖 Inspector 里配好的值）。</summary>
        public void ResolveReferences()
        {
            if (layout == null) layout = GetComponent<RoomLayout>();
            if (layout == null) layout = FindFirstObjectByType<RoomLayout>();
            if (anchor == null && layout != null) anchor = layout.anchor;
            if (anchor == null && layout != null) layout.ResolveReferences();
            if (anchor == null && layout != null) anchor = layout.anchor;
            if (anchor == null) anchor = transform;
        }

        private void LateUpdate()
        {
            RefreshDynamic();
        }

        /// <summary>
        /// 登记一个已落位物件。instance 可以为 null（例如从 layout.json 恢复的占位记录）。
        /// 超过容量时按 FIFO 淘汰，淘汰结果在 evicted 里返回。
        /// </summary>
        public PlacedItem Register(GameObject instance, string id, string objectName, RoomBand band,
            Vector3 localCenter, Vector3 size, float yawDegrees, bool dynamic, List<PlacedItem> evicted = null,
            string behavior = "", string sizeClass = "", LayoutIntent layoutIntent = null)
        {
            // 同一 id 再次落位（含 layout.json 回放）：先清掉旧记录与旧实例，再按当前占位重新排到队尾。
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_items[i].id, id, StringComparison.OrdinalIgnoreCase)) continue;

                if (_items[i].instance != null && _items[i].instance != instance) Destroy(_items[i].instance);
                _items.RemoveAt(i);
            }

            var item = new PlacedItem
            {
                id = id ?? string.Empty,
                objectName = objectName ?? string.Empty,
                band = band,
                localCenter = localCenter,
                size = size,
                yawDegrees = yawDegrees,
                dynamic = dynamic,
                addedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                behavior = behavior ?? string.Empty,
                sizeClass = sizeClass ?? string.Empty,
                intent = layoutIntent,
                instance = instance,
            };

            _items.Add(item);
            EnforceCapacity(evicted);
            Save();
            return item;
        }

        /// <summary>按当前场景变换刷新可推物件的位置（被恐龙推走后占用要跟着走）。</summary>
        public void RefreshDynamic()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                PlacedItem item = _items[i];
                if (!item.dynamic || item.instance == null || anchor == null) continue;

                item.localCenter = anchor.InverseTransformPoint(item.instance.transform.position);
            }
        }

        public bool TryGetItem(string id, out PlacedItem item)
        {
            item = null;
            if (string.IsNullOrWhiteSpace(id)) return false;

            foreach (PlacedItem candidate in _items)
            {
                if (string.Equals(candidate.id, id, StringComparison.OrdinalIgnoreCase))
                {
                    item = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 与已登记物件是否重叠：横向卷轴视角下只比 X 区间（物件宽度含旋转投影 + 余量）。
        /// 可推物件按「当前实际位置」参与判定。
        /// </summary>
        public bool HasOverlap(RoomBand band, string ignoreId, float localX, float width, float margin)
        {
            float halfWidth = Mathf.Abs(width) * 0.5f;
            float safeMargin = Mathf.Max(0f, margin);

            for (int i = 0; i < _items.Count; i++)
            {
                PlacedItem item = _items[i];
                if (item.band != band) continue;
                if (!string.IsNullOrWhiteSpace(ignoreId)
                    && string.Equals(item.id, ignoreId, StringComparison.OrdinalIgnoreCase)) continue;

                float otherX = CurrentCenter(item).x;
                float otherHalfWidth = Mathf.Abs(item.size.x) * 0.5f;

                if (Mathf.Abs(otherX - localX) < otherHalfWidth + halfWidth + safeMargin) return true;
            }

            return false;
        }

        /// <summary>移除登记（只释放占用，不销毁实例；需要销毁时调用方自己处理）。</summary>
        public bool Unregister(string id)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (!string.Equals(_items[i].id, id, StringComparison.OrdinalIgnoreCase)) continue;

                if (_items[i].instance != null) Destroy(_items[i].instance);
                _items.RemoveAt(i);
                Save();
                return true;
            }

            return false;
        }

        /// <summary>清空全部登记；destroyInstances = true 时同时销毁场景实例。</summary>
        public void ClearAll(bool destroyInstances)
        {
            foreach (PlacedItem item in _items)
            {
                if (destroyInstances && item.instance != null) Destroy(item.instance);
            }

            _items.Clear();
            Save();
        }

        private Vector3 CurrentCenter(PlacedItem item)
        {
            if (item.dynamic && item.instance != null && anchor != null)
                return anchor.InverseTransformPoint(item.instance.transform.position);

            return item.localCenter;
        }

        /// <summary>FIFO 淘汰：超过容量时反复移除最早加入的那条（列表顺序即加入顺序）。</summary>
        private void EnforceCapacity(List<PlacedItem> evicted)
        {
            int capacity = Capacity;
            while (_items.Count > capacity)
            {
                PlacedItem oldest = _items[0];
                _items.RemoveAt(0);

                Debug.Log($"[PrototypeC] 容量淘汰：{oldest.objectName}（{oldest.id}）｜加入于 {oldest.addedAt}｜" +
                          $"当前 {_items.Count}/{capacity}（模型缓存保留，可再次命中）");

                if (oldest.instance != null) Destroy(oldest.instance);
                evicted?.Add(oldest);
                Evicted?.Invoke(oldest);
            }
        }

        /// <summary>写 layout.json；失败只告警，不影响生成流程。</summary>
        public void Save()
        {
            if (!persist) return;

            try
            {
                Directory.CreateDirectory(ObjectCache.RootPath);
                var snapshot = new LayoutSnapshot { items = _items };
                File.WriteAllText(FilePath, JsonUtility.ToJson(snapshot, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PrototypeC] 布局落盘失败（{FilePath}）：{e.Message}");
            }
        }

        /// <summary>读 layout.json；只恢复占用记录，不恢复模型实例（instance 为 null）。</summary>
        public void Load()
        {
            if (!persist) return;

            try
            {
                if (!File.Exists(FilePath)) return;

                LayoutSnapshot snapshot = JsonUtility.FromJson<LayoutSnapshot>(File.ReadAllText(FilePath));
                if (snapshot == null || snapshot.items == null) return;

                _items.Clear();
                foreach (PlacedItem item in snapshot.items)
                {
                    if (item == null) continue;
                    item.instance = null;

                    // JsonUtility 会把 null 的对象字段还原成空实例；没有锚点/分区的意图等于没有意图
                    if (item.intent != null
                        && string.IsNullOrWhiteSpace(item.intent.anchor)
                        && string.IsNullOrWhiteSpace(item.intent.band))
                    {
                        item.intent = null;
                    }

                    _items.Add(item);
                }

                if (_items.Count > 0)
                    Debug.Log($"[PrototypeC] 布局回放：从 {FilePath} 恢复 {_items.Count} 条占用记录（模型实例需重新加载）");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PrototypeC] 布局读取失败（{FilePath}）：{e.Message}");
            }
        }

        [Serializable]
        private class LayoutSnapshot
        {
            public int version = 1;
            public List<PlacedItem> items = new List<PlacedItem>();
        }
    }
}
