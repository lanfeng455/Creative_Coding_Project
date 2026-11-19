using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>Opt-in Play Mode integration check; never runs during normal gameplay.</summary>
public static class AFrameConstructionCheck
{
    private static DinoAgentB1 _dino;
    private static TypeWriterB1 _writer;
    private static B2House _house;
    private static int _stage;
    private static bool _waiting;
    private static bool _interrupted, _rebuilding;
    private static double _deadline, _captureAfter;
    private static string _folder;
    private static B2PlayCheckScope _scope;
    public static bool Running { get; private set; }
    public static string Status { get; private set; } = "Not run";

    public static void Begin()
    {
        if (!EditorApplication.isPlaying || Running) throw new InvalidOperationException("Start a fresh Play session first.");
        _dino = UnityEngine.Object.FindFirstObjectByType<DinoAgentB1>();
        _writer = UnityEngine.Object.FindFirstObjectByType<TypeWriterB1>();
        _house = UnityEngine.Object.FindFirstObjectByType<B2House>();
        if (_dino == null || _writer == null || _house == null || _house.WoodStepsBuilt != 0)
            throw new InvalidOperationException("A fresh B2 scene with an unbuilt house is required.");
        _folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../output/aframe-house-v01"));
        Directory.CreateDirectory(_folder);
        _interrupted = _rebuilding = false;
        _scope = new B2PlayCheckScope(_writer);
        _stage = 0; _waiting = false; _deadline = EditorApplication.timeSinceStartup + 25;
        Running = true; Status = "Running";
        EditorApplication.update += Tick;
    }

    private static int Deliveries()
    {
        int count = 0;
        foreach (var r in B1Resource.All)
            if (r != null && r.Alive && r.IsConstructionLog) count++;
        return count;
    }

    private static void Tick()
    {
        try
        {
            if (!EditorApplication.isPlaying) { End("Stopped"); return; }
            EditorApplication.QueuePlayerLoopUpdate();
            if (EditorApplication.timeSinceStartup > _deadline) throw new Exception("Timeout stage " + _stage + "; request=" + _dino.RequestedWord + "; state=" + _dino.CurrentState);
            string request = _dino.RequestedWord;
            if (request == WindEncounter.CounterWord || request == FireEncounter.CounterWord || request == SeedEncounter.CounterWord)
            {
                for (int i = request.Length - 1; i >= 0; i--) _writer.Add(request[i]);
                _deadline = EditorApplication.timeSinceStartup + 50;
                return;
            }
            if (_waiting)
            {
                if (!_interrupted && _stage == 1 && _dino.CurrentState == DinoAgentB1.State.Building)
                {
                    _interrupted = true;
                    _dino.Scare(_dino.transform.position.x - 3f);
                    if (_dino.RequestedWord != null) throw new Exception("A fleeing carrier requested a duplicate material.");
                    _deadline = EditorApplication.timeSinceStartup + 40;
                }
                if (_house.WoodStepsBuilt != _stage + 1) return;
                if (_captureAfter == 0) _captureAfter = EditorApplication.timeSinceStartup + 1.2;
                if (EditorApplication.timeSinceStartup < _captureAfter) return;
                if (_rebuilding)
                {
                    if (_house.WoodStepsBuilt != 1 || _house.NextConstructionWord != "WOOD")
                        throw new Exception("Destroyed house did not rebuild from its first stage.");
                    End("PASS: six materials, invalid and incomplete words, delivery reservation, repeated GLASS letters, natural collection/building, scare/resume, completion, destruction/rebuild, six camera captures.");
                    return;
                }
                Capture("stage-" + (_stage + 1) + "-" + AFrameBuildPlan.WordAt(_stage) + ".png", true);
                _stage++; _waiting = false; _captureAfter = 0;
                _deadline = EditorApplication.timeSinceStartup + 25;
                if (_stage == AFrameBuildPlan.StageCount)
                {
                    if (_house.Rooms.Count != 1 || _house.NextConstructionWord != null)
                        throw new Exception("House completion did not register exactly one room.");
                    Capture("game-completed.png", false);
                    if (!_interrupted) throw new Exception("Carrier interruption was not exercised.");
                    var completedRoom = _house.Rooms[0];
                    for (int hit = 0; hit < Mathf.Max(1, _house.roomHp); hit++)
                        _house.DestroyRoom(completedRoom);
                    if (_house.WoodStepsBuilt != 0 || _house.NextConstructionWord != "STONE")
                        throw new Exception("Destroyed house did not reset its material plan.");
                    _stage = 0; _rebuilding = true;
                    return;
                }
            }
            string word = AFrameBuildPlan.WordAt(_stage);
            if (_dino.RequestedWord != word) return;
            _writer.Clear();
            if (_stage == 0)
            {
                foreach (char c in "MUD") _writer.Add(c);
                if (Deliveries() != 0 || _house.WoodStepsBuilt != 0) throw new Exception("Wrong word generated a material.");
                _writer.Clear();
            }
            for (int i = 0; i < word.Length - 1; i++) _writer.Add(word[i]);
            if (Deliveries() != 0) throw new Exception("Incomplete word generated a material: " + word);
            _writer.Add(word[word.Length - 1]);
            if (Deliveries() != 1 || _dino.RequestedWord != null) throw new Exception("Expected one reserved delivery for " + word);
            // Additional valid words intentionally craft stock; reservation is
            // verified by RequestedWord above, not by rejecting player input.
            _writer.Clear();
            _waiting = true;
            Status = "Building " + (_stage + 1) + "/6 " + word;
        }
        catch (Exception ex) { End("FAIL: " + ex); }
    }

    private static void Capture(string filename, bool closeup)
    {
        Camera source = Camera.main;
        if (source == null) throw new Exception("Main camera missing.");
        var go = new GameObject("AFrame QA Camera");
        var camera = go.AddComponent<Camera>();
        camera.CopyFrom(source); camera.enabled = false;
        camera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
        int width = closeup ? 1100 : 1920, height = closeup ? 900 : 1080;
        if (closeup)
        {
            camera.orthographic = true; camera.orthographicSize = 1.85f;
            camera.transform.position = new Vector3(0.15f, _dino.world.upperFloorY + 1.3f, source.transform.position.z);
        }
        var target = new RenderTexture(width, height, 24);
        var previous = RenderTexture.active;
        Texture2D image = null;
        try
        {
            camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
            image = new Texture2D(width, height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0); image.Apply();
            File.WriteAllBytes(Path.Combine(_folder, filename), image.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous; camera.targetTexture = null;
            target.Release(); UnityEngine.Object.DestroyImmediate(target);
            if (image != null) UnityEngine.Object.DestroyImmediate(image);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static void End(string message)
    {
        EditorApplication.update -= Tick;
        _scope?.Dispose(); _scope = null;
        Running = false; Status = message;
        File.WriteAllText(Path.Combine(_folder, "integration-check.txt"), message);
        if (message.StartsWith("FAIL")) Debug.LogError(message); else Debug.Log(message);
    }
}
