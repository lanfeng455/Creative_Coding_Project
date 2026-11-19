// PrototypeCCacheTool.cs —— 一次性把已有本机缓存发布到工程镜像
// 菜单：Tools/Prototype C/Publish Local Cache To Project Mirror
// 只读本机缓存、写 ProjectMirror/，不修改场景、不启动 Play。

using System;
using System.IO;
using PrototypeC;
using UnityEditor;
using UnityEngine;

namespace PrototypeC.EditorTools
{
    public static class PrototypeCCacheTool
    {
        [MenuItem("Tools/Prototype C/Publish Local Cache To Project Mirror")]
        public static void PublishLocalCache()
        {
            if (!Directory.Exists(ObjectCache.RootPath))
            {
                EditorUtility.DisplayDialog(
                    "Prototype C",
                    $"本机缓存目录不存在：\n{ObjectCache.RootPath}",
                    "好");
                return;
            }

            string[] folders = Directory.GetDirectories(ObjectCache.RootPath);
            int written = 0;
            int skipped = 0;
            int failed = 0;

            foreach (string folder in folders)
            {
                string normalizedId = Path.GetFileName(folder);
                ObjectCache.MirrorResult result = ObjectCache.MirrorToProject(normalizedId, out string detail);
                switch (result)
                {
                    case ObjectCache.MirrorResult.Written:
                        written++;
                        Debug.Log($"[PrototypeC] 镜像已写入 {normalizedId}：{detail}");
                        break;
                    case ObjectCache.MirrorResult.Skipped:
                        skipped++;
                        break;
                    default:
                        failed++;
                        Debug.LogWarning($"[PrototypeC] 镜像写入失败 {normalizedId}：{detail}");
                        break;
                }
            }

            EditorUtility.DisplayDialog(
                "Prototype C",
                $"工程镜像同步完成\n\n写入 {written}｜已一致跳过 {skipped}｜失败 {failed}\n\n镜像目录：\n{ObjectCache.ProjectMirrorRoot}",
                "好");
        }
    }
}
