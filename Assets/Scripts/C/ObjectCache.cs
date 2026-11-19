// ObjectCache.cs —— Prototype C：本地模型缓存 + 工程内缓存镜像（按生成参数分档）
//
// 目录结构（同一 id 可以并存多档，切快速/标准模式互不覆盖）：
//   <persistentDataPath>/ObjectCache/<normalizedId>/<tier>/model.glb + metadata.json   ← 新格式
//   <persistentDataPath>/ObjectCache/<normalizedId>/model.glb + metadata.json         ← 旧平铺格式（继续可读）
//   tier = generationFingerprint 的 FNV-1a 64 哈希前 8 位（小写十六进制），同一套参数恒定映射到同一档。
// 工程镜像：<工程根>/PrototypeC_Cache/<normalizedId>/<tier>/…，结构与本机一一对应；播种/回写都按档处理。
//
// 命中规则：
//   1. 先找「当前指纹对应的档」：分档目录里的同指纹副本最快命中；旧平铺副本指纹一致也算精确命中；
//   2. 精确档不存在时回退到同 id 下「最近创建的一档」，由调用方在状态栏标注“当前显示的是另一档参数生成的模型”；
//   3. 镜像不参与 TryGet 判定，只在本机未命中时按同样的分档规则播种。
//   4. 写入只写当前指纹那一档，不删除、不覆盖其它档；旧平铺副本原样保留（并存，不迁移、不清理）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace PrototypeC
{
    /// <summary>随模型一起落盘的物件元数据。</summary>
    [Serializable]
    public class ObjectMetadata
    {
        public string objectName;
        public string prompt;
        public string normalizedId;
        public string behavior;

        // —— LLM 物件定义（本阶段只存储与展示，不参与布局）——
        public string placementKind;
        public string sizeClass;

        public Vector3 placement;
        public float yawDegrees;
        public Vector3 size;

        public string aiModel;
        public int targetPolycount;
        public bool shouldRemesh;
        public bool enableTexture;
        public string textureResolution;
        public string modelType;
        public string textureModel;
        public bool enablePbr;
        public string generationFingerprint;

        public string previewTaskId;
        public string refineTaskId;

        // —— 风格层追溯 ——
        public string styleId;
        public int styleVersion;
        public string styleFingerprint;
        public bool promptTrimmed;
        public string promptTrimNote;

        public long glbBytes;
        public string createdAtUtc;

        // —— 工程镜像追溯字段 ——
        /// <summary>generated-local（本机生成）/ seeded-from-mirror（从工程镜像播种）。</summary>
        public string cacheOrigin;
        /// <summary>若该副本来自工程镜像播种，记录播种时间（UTC ISO8601）。</summary>
        public string seededFromProjectMirrorAtUtc;
        /// <summary>最近一次同步到工程镜像的时间（UTC ISO8601）。</summary>
        public string mirrorSyncedAtUtc;
        /// <summary>镜像中该 GLB 的字节数。</summary>
        public long mirrorBytes;
        /// <summary>覆盖镜像前发现两边不一致时的说明；空字符串表示无冲突。</summary>
        public string mirrorConflictNote;
    }

    public static class ObjectCache
    {
        /// <summary>缓存里的一档（一个 id + 一份生成参数）。</summary>
        public class CacheEntry
        {
            public string id;
            /// <summary>档位名：8 位十六进制指纹哈希，或 "legacy"（旧平铺格式）。</summary>
            public string tier;
            public string folder;
            public string modelPath;
            public string metadataPath;
            public ObjectMetadata metadata;
            public long bytes;
            public DateTime createdUtc;
            public bool isLegacy;

            public string fingerprint => metadata != null ? metadata.generationFingerprint : null;
        }

        public const string RootFolderName = "ObjectCache";
        public const string ModelFileName = "model.glb";
        public const string MetadataFileName = "metadata.json";

        public const string ProjectMirrorFolderName = "PrototypeC_Cache";

        public const string OriginGeneratedLocal = "generated-local";
        public const string OriginSeededFromMirror = "seeded-from-mirror";

        /// <summary>旧平铺格式（<id>/model.glb）在 CacheEntry.tier 里的标记名。</summary>
        public const string LegacyTier = "legacy";

        public static string RootPath => Path.Combine(Application.persistentDataPath, RootFolderName);

        public static string GetFolder(string normalizedId) => Path.Combine(RootPath, normalizedId);
        /// <summary>旧平铺格式的模型路径（只用于兼容读取 / 迁移对照，新写入请用 GetTierFolder）。</summary>
        public static string GetModelPath(string normalizedId) => Path.Combine(GetFolder(normalizedId), ModelFileName);
        /// <summary>旧平铺格式的 metadata 路径。</summary>
        public static string GetMetadataPath(string normalizedId) => Path.Combine(GetFolder(normalizedId), MetadataFileName);

        public static string GetTierFolder(string normalizedId, string tier) => Path.Combine(GetFolder(normalizedId), tier);
        public static string GetTierModelPath(string normalizedId, string tier) => Path.Combine(GetTierFolder(normalizedId, tier), ModelFileName);
        public static string GetTierMetadataPath(string normalizedId, string tier) => Path.Combine(GetTierFolder(normalizedId, tier), MetadataFileName);

        /// <summary>由生成参数算档位名（与 generationFingerprint 一一对应，稳定不变）。</summary>
        public static string GetTierKey(GenerationParams generation) =>
            generation == null ? LegacyTier : GetTierKey(generation.ToCacheFingerprint());

        /// <summary>由完整指纹算档位名：FNV-1a 64 的前 8 位十六进制。</summary>
        public static string GetTierKey(string generationFingerprint)
        {
            if (string.IsNullOrEmpty(generationFingerprint)) return LegacyTier;

            unchecked
            {
                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < generationFingerprint.Length; i++)
                {
                    hash ^= generationFingerprint[i];
                    hash *= 1099511628211UL;
                }
                return hash.ToString("x16").Substring(0, 8);
            }
        }

        private static bool IsTierName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length != 8) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>工程内镜像根目录（Assets 之外，避免 Unity 导入 GLB）。</summary>
        public static string ProjectMirrorRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", ProjectMirrorFolderName));

        public static string GetProjectMirrorFolder(string normalizedId) => Path.Combine(ProjectMirrorRoot, normalizedId);
        public static string GetProjectMirrorModelPath(string normalizedId) => Path.Combine(GetProjectMirrorFolder(normalizedId), ModelFileName);
        public static string GetProjectMirrorMetadataPath(string normalizedId) => Path.Combine(GetProjectMirrorFolder(normalizedId), MetadataFileName);
        public static string GetProjectMirrorTierFolder(string normalizedId, string tier) => Path.Combine(GetProjectMirrorFolder(normalizedId), tier);

        // —— 缓存别名 ——
        // 存放位置：persistentDataPath/ObjectCache/aliases.json（不在任何 id 子目录里，不动已有缓存内容）。
        // 格式：{"aliases":[{"from":"plant","to":"pothos","note":"..."}]}
        // 读取失败（JSON 损坏、权限问题）按「没有别名」处理并打一次警告，不影响正常生成。
        public const string AliasFileName = "aliases.json";

        [Serializable]
        public class CacheAlias
        {
            public string from;
            public string to;
            public string note;
        }

        [Serializable]
        public class CacheAliasTable
        {
            public List<CacheAlias> aliases = new List<CacheAlias>();
        }

        private static List<CacheAlias> _aliases;

        public static string AliasPath => Path.Combine(RootPath, AliasFileName);

        public static List<CacheAlias> LoadAliases(bool forceReload = false)
        {
            if (_aliases != null && !forceReload) return _aliases;

            _aliases = new List<CacheAlias>();
            try
            {
                if (File.Exists(AliasPath))
                {
                    string json = File.ReadAllText(AliasPath);
                    CacheAliasTable table = JsonUtility.FromJson<CacheAliasTable>(json);
                    if (table != null && table.aliases != null) _aliases = table.aliases;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PrototypeC] 别名表读取失败，按无别名处理：{AliasPath}｜{ex.Message}");
            }

            return _aliases;
        }

        public static bool SaveAliases(List<CacheAlias> aliases)
        {
            try
            {
                Directory.CreateDirectory(RootPath);
                var table = new CacheAliasTable { aliases = aliases ?? new List<CacheAlias>() };
                File.WriteAllText(AliasPath, JsonUtility.ToJson(table, true));
                _aliases = table.aliases;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PrototypeC] 别名表写入失败：{AliasPath}｜{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 把规范化 id 解析成别名目标；没有别名时原样返回。
        /// 最多跟 4 层，A→B→A 这类环会在重复处停下，不会死循环。
        /// </summary>
        public static string ResolveAlias(string normalizedId)
        {
            if (string.IsNullOrWhiteSpace(normalizedId)) return normalizedId;

            List<CacheAlias> aliases = LoadAliases();
            string current = normalizedId.Trim();

            for (int depth = 0; depth < 4; depth++)
            {
                string next = null;
                for (int i = 0; i < aliases.Count; i++)
                {
                    CacheAlias alias = aliases[i];
                    if (alias == null || string.IsNullOrWhiteSpace(alias.from) || string.IsNullOrWhiteSpace(alias.to)) continue;
                    if (!string.Equals(alias.from.Trim(), current, StringComparison.OrdinalIgnoreCase)) continue;

                    next = alias.to.Trim();
                    break;
                }

                if (string.IsNullOrEmpty(next) || string.Equals(next, current, StringComparison.OrdinalIgnoreCase)) break;
                current = next;
            }

            return current;
        }

        // —— 分档扫描 ——

        /// <summary>
        /// 扫一个 id 目录下的所有可用档位：旧平铺（&lt;id&gt;/model.glb）+ 8 位指纹子目录。
        /// 每一档都校验 model.glb 非空、metadata 可读、normalizedId 一致。
        /// </summary>
        public static List<CacheEntry> ScanEntries(string idFolder, string normalizedId, bool logProblems = false)
        {
            var entries = new List<CacheEntry>();
            if (string.IsNullOrEmpty(idFolder) || !Directory.Exists(idFolder)) return entries;

            TryAddEntry(entries, idFolder, normalizedId, true, logProblems);

            string[] subFolders;
            try
            {
                subFolders = Directory.GetDirectories(idFolder);
            }
            catch (Exception ex)
            {
                if (logProblems) Debug.LogWarning($"[PrototypeC] 读取缓存档位失败：{idFolder}｜{ex.Message}");
                return entries;
            }

            foreach (string sub in subFolders)
            {
                string tier = Path.GetFileName(sub);
                if (!IsTierName(tier)) continue;
                TryAddEntry(entries, sub, normalizedId, false, logProblems);
            }

            // 判重：同一指纹同时存在「分档副本」和「旧平铺副本」时只保留分档副本，
            // 避免同一 id 同一份参数被当成两个物件（镜像会重复写、缓存库会重复列）。
            if (entries.Count > 1)
            {
                var tierFingerprints = new HashSet<string>(StringComparer.Ordinal);
                foreach (CacheEntry entry in entries)
                {
                    if (!entry.isLegacy && !string.IsNullOrEmpty(entry.fingerprint)) tierFingerprints.Add(entry.fingerprint);
                }

                if (tierFingerprints.Count > 0)
                {
                    entries.RemoveAll(entry =>
                        entry.isLegacy && !string.IsNullOrEmpty(entry.fingerprint) && tierFingerprints.Contains(entry.fingerprint));
                }
            }

            return entries;
        }

        private static void TryAddEntry(List<CacheEntry> entries, string folder, string normalizedId, bool isLegacy, bool logProblems)
        {
            string modelPath = Path.Combine(folder, ModelFileName);
            string metadataPath = Path.Combine(folder, MetadataFileName);

            if (!File.Exists(modelPath)) return;
            long bytes;
            try
            {
                bytes = new FileInfo(modelPath).Length;
            }
            catch
            {
                return;
            }
            if (bytes <= 0 || !File.Exists(metadataPath)) return;

            ObjectMetadata meta;
            try
            {
                meta = JsonUtility.FromJson<ObjectMetadata>(File.ReadAllText(metadataPath));
            }
            catch (Exception ex)
            {
                if (logProblems) Debug.LogWarning($"[PrototypeC] metadata 解析失败（该档跳过）：{metadataPath}｜{ex.Message}");
                return;
            }

            if (meta == null) return;
            if (!string.Equals(meta.normalizedId, normalizedId, StringComparison.Ordinal)) return;

            entries.Add(new CacheEntry
            {
                id = normalizedId,
                tier = isLegacy ? LegacyTier : Path.GetFileName(folder),
                folder = folder,
                modelPath = modelPath,
                metadataPath = metadataPath,
                metadata = meta,
                bytes = bytes,
                createdUtc = ParseCreatedUtc(meta, modelPath),
                isLegacy = isLegacy,
            });
        }

        private static DateTime ParseCreatedUtc(ObjectMetadata meta, string modelPath)
        {
            if (meta != null && !string.IsNullOrEmpty(meta.createdAtUtc)
                && DateTime.TryParse(meta.createdAtUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime parsed))
            {
                return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
            }

            try
            {
                return File.GetLastWriteTimeUtc(modelPath);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>选档：先精确匹配指纹（优先分档目录，其次旧平铺），否则回退到最近创建的一档。</summary>
        public static CacheEntry SelectEntry(List<CacheEntry> entries, string targetFingerprint, out bool exact)
        {
            exact = false;
            if (entries == null || entries.Count == 0) return null;

            if (!string.IsNullOrEmpty(targetFingerprint))
            {
                string tier = GetTierKey(targetFingerprint);

                for (int i = 0; i < entries.Count; i++)
                {
                    CacheEntry entry = entries[i];
                    if (entry.isLegacy) continue;
                    if (!string.Equals(entry.tier, tier, StringComparison.Ordinal)) continue;
                    if (!string.Equals(entry.fingerprint, targetFingerprint, StringComparison.Ordinal)) continue;
                    exact = true;
                    return entry;
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    CacheEntry entry = entries[i];
                    if (!string.Equals(entry.fingerprint, targetFingerprint, StringComparison.Ordinal)) continue;
                    exact = true;
                    return entry;
                }
            }

            CacheEntry newest = null;
            for (int i = 0; i < entries.Count; i++)
            {
                CacheEntry entry = entries[i];
                if (newest == null || entry.createdUtc > newest.createdUtc) newest = entry;
            }
            return newest;
        }

        public static CacheEntry SelectNewest(List<CacheEntry> entries)
        {
            CacheEntry newest = null;
            if (entries == null) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                CacheEntry entry = entries[i];
                if (newest == null || entry.createdUtc > newest.createdUtc) newest = entry;
            }
            return newest;
        }

        public enum CacheHitKind
        {
            /// <summary>没有任何可用档位。</summary>
            Miss,
            /// <summary>当前指纹完全一致。</summary>
            Exact,
            /// <summary>同 id 下其它档位的模型（参数不同，需在状态栏提示）。</summary>
            Fallback,
        }

        /// <summary>
        /// 缓存查询：先精确匹配当前指纹，找不到时回退到同 id 下最近一档（Fallback）。
        /// 只看本机缓存；工程镜像不参与判定。
        /// </summary>
        public static CacheHitKind TryGet(string normalizedId, GenerationParams generation, out CacheEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(normalizedId)) return CacheHitKind.Miss;

            List<CacheEntry> entries = ScanEntries(GetFolder(normalizedId), normalizedId, true);
            string target = generation != null ? generation.ToCacheFingerprint() : string.Empty;
            entry = SelectEntry(entries, target, out bool exact);
            if (entry == null) return CacheHitKind.Miss;
            if (exact) return CacheHitKind.Exact;

            Debug.LogWarning($"[PrototypeC] 缓存降级：{normalizedId} 没有「{target}」这一档，" +
                             $"改用 {DescribeTier(entry)}（创建于 {entry.metadata.createdAtUtc}）；" +
                             "当前显示的是另一档参数生成的模型");
            return CacheHitKind.Fallback;
        }

        /// <summary>下载完成后先落 GLB 再写 metadata，保证 metadata 存在即代表 GLB 完整。只写当前指纹那一档。</summary>
        public static string Save(string normalizedId, string downloadedModelPath, ObjectMetadata metadata)
        {
            string tier = GetTierKey(metadata != null ? metadata.generationFingerprint : null);
            string folder = tier == LegacyTier ? GetFolder(normalizedId) : GetTierFolder(normalizedId, tier);
            Directory.CreateDirectory(folder);

            string targetModelPath = Path.Combine(folder, ModelFileName);
            File.Copy(downloadedModelPath, targetModelPath, true);

            metadata.normalizedId = normalizedId;
            metadata.glbBytes = new FileInfo(targetModelPath).Length;
            metadata.createdAtUtc = DateTime.UtcNow.ToString("o");
            metadata.cacheOrigin = OriginGeneratedLocal;

            File.WriteAllText(Path.Combine(folder, MetadataFileName), JsonUtility.ToJson(metadata, true));

            // 旧平铺副本继续保留（并存，不迁移、不删除），这里只提示一下便于排查
            string legacyModelPath = GetModelPath(normalizedId);
            if (File.Exists(legacyModelPath))
            {
                Debug.Log($"[PrototypeC] 缓存分档：{normalizedId} 新模型写入档 {tier}；" +
                          "旧平铺副本保留不动（读取时仍可命中，不再新写）");
            }

            return folder;
        }

        /// <summary>
        /// 本机缓存缺失时，从工程镜像按分档规则播种到本机（精确档优先，否则最近一档）。
        /// 成功返回 true，并回填播种后的 metadata、本机模型路径、是否为精确档；不会调用 Meshy。
        /// </summary>
        public static bool TrySeedFromProjectMirror(
            string normalizedId,
            GenerationParams generation,
            out ObjectMetadata metadata,
            out string seededModelPath,
            out bool exact,
            out string reason)
        {
            metadata = null;
            seededModelPath = string.Empty;
            exact = false;
            reason = string.Empty;

            if (!Directory.Exists(ProjectMirrorRoot))
            {
                reason = $"工程镜像目录不存在：{ProjectMirrorRoot}";
                return false;
            }

            List<CacheEntry> entries = ScanEntries(GetProjectMirrorFolder(normalizedId), normalizedId, false);
            if (entries.Count == 0)
            {
                reason = "工程镜像中无该物件";
                return false;
            }

            string target = generation != null ? generation.ToCacheFingerprint() : string.Empty;
            CacheEntry pick = SelectEntry(entries, target, out exact);
            if (pick == null)
            {
                reason = "工程镜像副本不可用（GLB 为空 / metadata 不可读 / 标识或生成参数不一致）";
                return false;
            }

            try
            {
                // 播种保持镜像里的档位结构：镜像旧平铺 → 本机旧平铺；镜像分档 → 本机同名分档
                string folder = pick.isLegacy ? GetFolder(normalizedId) : GetTierFolder(normalizedId, pick.tier);
                Directory.CreateDirectory(folder);

                string localModelPath = Path.Combine(folder, ModelFileName);
                File.Copy(pick.modelPath, localModelPath, true);

                ObjectMetadata seeded = pick.metadata;
                seeded.normalizedId = normalizedId;
                seeded.glbBytes = new FileInfo(localModelPath).Length;
                seeded.cacheOrigin = OriginSeededFromMirror;
                seeded.seededFromProjectMirrorAtUtc = DateTime.UtcNow.ToString("o");
                if (string.IsNullOrEmpty(seeded.createdAtUtc)) seeded.createdAtUtc = seeded.seededFromProjectMirrorAtUtc;

                File.WriteAllText(Path.Combine(folder, MetadataFileName), JsonUtility.ToJson(seeded, true));

                metadata = seeded;
                seededModelPath = localModelPath;
                reason = exact
                    ? $"已从工程镜像播种（同档 {DescribeTier(pick)}）"
                    : $"已从工程镜像播种（另一档 {DescribeTier(pick)}）";
                return true;
            }
            catch (Exception e)
            {
                reason = "播种失败：" + e.Message;
                return false;
            }
        }

        public enum MirrorResult
        {
            /// <summary>镜像已是相同内容，未重复写。</summary>
            Skipped,
            /// <summary>已写入（含覆盖不一致的旧镜像）。</summary>
            Written,
            /// <summary>写入失败（不影响本机缓存与生成结果）。</summary>
            Failed,
        }

        /// <summary>
        /// 把本机该 id 的所有档位发布到工程镜像（结构一一对应，互不覆盖）。
        /// 内容一致时跳过；不一致时以本机为准覆盖该档，并在 metadata 记录旧镜像信息。
        /// </summary>
        public static MirrorResult MirrorToProject(string normalizedId, out string detail)
        {
            detail = string.Empty;

            List<CacheEntry> entries = ScanEntries(GetFolder(normalizedId), normalizedId, false);
            if (entries.Count == 0)
            {
                detail = "本机缓存不存在，无法同步";
                return MirrorResult.Failed;
            }

            int written = 0;
            int skipped = 0;
            var errors = new List<string>();

            foreach (CacheEntry entry in entries)
            {
                if (MirrorOne(normalizedId, entry, out string oneDetail, out bool wrote))
                {
                    if (wrote) written++;
                    else skipped++;
                }
                else
                {
                    errors.Add($"{entry.tier}：{oneDetail}");
                }
            }

            detail = $"档位 {entries.Count}（写入 {written} / 一致 {skipped} / 失败 {errors.Count}）";
            if (errors.Count > 0)
            {
                detail += "｜" + string.Join("；", errors);
                return MirrorResult.Failed;
            }

            return written > 0 ? MirrorResult.Written : MirrorResult.Skipped;
        }

        private static bool MirrorOne(string normalizedId, CacheEntry entry, out string detail, out bool wrote)
        {
            detail = string.Empty;
            wrote = false;

            if (entry == null || entry.metadata == null)
            {
                detail = "metadata 缺失";
                return false;
            }

            try
            {
                string mirrorFolder = entry.isLegacy
                    ? GetProjectMirrorFolder(normalizedId)
                    : GetProjectMirrorTierFolder(normalizedId, entry.tier);
                string mirrorModelPath = Path.Combine(mirrorFolder, ModelFileName);
                string mirrorMetadataPath = Path.Combine(mirrorFolder, MetadataFileName);

                bool mirrorExists = File.Exists(mirrorModelPath) && File.Exists(mirrorMetadataPath);
                if (mirrorExists && FileContentEquals(entry.modelPath, mirrorModelPath))
                {
                    return true;   // 一致，跳过
                }

                string conflictNote = string.Empty;
                if (File.Exists(mirrorModelPath))
                {
                    long oldBytes = new FileInfo(mirrorModelPath).Length;
                    string oldSynced = string.Empty;
                    try
                    {
                        ObjectMetadata oldMeta = JsonUtility.FromJson<ObjectMetadata>(File.ReadAllText(mirrorMetadataPath));
                        if (oldMeta != null) oldSynced = oldMeta.mirrorSyncedAtUtc ?? oldMeta.createdAtUtc ?? string.Empty;
                    }
                    catch
                    {
                        // 旧 metadata 损坏不影响覆盖
                    }

                    conflictNote = $"覆盖前镜像 {oldBytes} 字节（同步于 {oldSynced}），以本机版本为准";
                    Debug.LogWarning($"[PrototypeC] 工程镜像与本机缓存不一致，按“本机优先”覆盖：{normalizedId}｜档 {entry.tier}");
                }

                Directory.CreateDirectory(mirrorFolder);

                string now = DateTime.UtcNow.ToString("o");

                // 先写临时文件再替换，避免中途失败留下半个 GLB
                string tempModelPath = mirrorModelPath + ".tmp";
                File.Copy(entry.modelPath, tempModelPath, true);
                long bytes = new FileInfo(tempModelPath).Length;
                if (File.Exists(mirrorModelPath)) File.Delete(mirrorModelPath);
                File.Move(tempModelPath, mirrorModelPath);

                ObjectMetadata meta = entry.metadata;
                meta.mirrorSyncedAtUtc = now;
                meta.mirrorBytes = bytes;
                meta.mirrorConflictNote = conflictNote;
                File.WriteAllText(mirrorMetadataPath, JsonUtility.ToJson(meta, true));

                // 本机 metadata 也记录同步信息，便于从任意一边追溯
                File.WriteAllText(entry.metadataPath, JsonUtility.ToJson(meta, true));

                detail = $"已写入 {mirrorModelPath}（{bytes} 字节）";
                wrote = true;
                return true;
            }
            catch (Exception e)
            {
                detail = e.Message;
                return false;
            }
        }

        /// <summary>状态栏/日志用的一行档位描述。</summary>
        public static string DescribeTier(CacheEntry entry)
        {
            if (entry == null) return "无缓存";
            string tierText = entry.isLegacy ? "旧平铺" : entry.tier;
            string fp = string.IsNullOrEmpty(entry.fingerprint) ? "指纹未记录" : entry.fingerprint;
            return $"{entry.id}/{tierText}（{fp}）";
        }

        private static bool FileContentEquals(string pathA, string pathB)
        {
            var a = new FileInfo(pathA);
            var b = new FileInfo(pathB);
            if (!a.Exists || !b.Exists) return false;
            if (a.Length != b.Length) return false;

            return string.Equals(ComputeSha256(pathA), ComputeSha256(pathB), StringComparison.Ordinal);
        }

        private static string ComputeSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
