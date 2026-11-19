using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>Opt-in verification of unordered crafting through the actual B2 delivery loop.</summary>
public static class UnorderedWordCheck
{
    private static TypeWriterB1 _writer;
    private static DinoAgentB1 _dino;
    private static B2House _house;
    private static int _stage;
    private static bool _waiting;
    private static B2PlayCheckScope _scope;
    private static double _deadline;
    public static string Status { get; private set; } = "Not run";

    public static void Begin()
    {
        if (!Application.isPlaying) throw new InvalidOperationException("Enter a fresh TemplateB2 Play session first.");
        _writer = UnityEngine.Object.FindFirstObjectByType<TypeWriterB1>();
        _dino = UnityEngine.Object.FindFirstObjectByType<DinoAgentB1>();
        _house = UnityEngine.Object.FindFirstObjectByType<B2House>();
        if (_writer == null || _dino == null || _house == null || _house.WoodStepsBuilt != 0)
            throw new InvalidOperationException("A fresh B2 session is required.");
        if (TypeWriterB1.MissingLetters("STONE", "XENOT") != "S" ||
            TypeWriterB1.MissingLetters("GLASS", "XSALG") != "S")
            throw new Exception("Missing-letter counts are incorrect.");
        _scope?.Dispose();
        _scope = new B2PlayCheckScope(_writer);
        _stage = 0; _waiting = false;
        _deadline = EditorApplication.timeSinceStartup + 40;
        Status = "Running";
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }

    private static void Tick()
    {
        try
        {
            if (!Application.isPlaying) { End("Stopped"); return; }
            EditorApplication.QueuePlayerLoopUpdate();
            if (EditorApplication.timeSinceStartup > _deadline)
                throw new Exception("Timeout: stage=" + _stage + ", request=" + _dino.RequestedWord);
            string request = _dino.RequestedWord;
            if (request == WindEncounter.CounterWord || request == FireEncounter.CounterWord || request == SeedEncounter.CounterWord)
            {
                for (int i = request.Length - 1; i >= 0; i--) _writer.Add(request[i]);
                _deadline = EditorApplication.timeSinceStartup + 50;
                return;
            }
            if (_waiting)
            {
                if (_house.WoodStepsBuilt != _stage + 1) return;
                _stage++; _waiting = false;
                _deadline = EditorApplication.timeSinceStartup + 40;
                if (_stage == AFrameBuildPlan.StageCount)
                {
                    End("PASS: all six unordered words, incomplete words rejected, GLASS requires two S, extra X retained, correct materials spawned, natural collection and six building stages completed.");
                    return;
                }
            }
            string word = AFrameBuildPlan.WordAt(_stage);
            if (_dino.RequestedWord != word) return;
            _writer.Clear();
            _writer.Add('X');
            // Keep one S until last for GLASS; other words are entered backwards.
            string input = word == "GLASS" ? "SALGS" : Reverse(word);
            for (int i = 0; i < input.Length - 1; i++) _writer.Add(input[i]);
            if (_writer.BufferText != "X" + input.Substring(0, input.Length - 1))
                throw new Exception("Incomplete word consumed: " + word);
            _writer.Add(input[input.Length - 1]);
            if (_writer.BufferText != "X") throw new Exception("Extra letters lost or word not consumed: " + word);
            int matches = 0;
            foreach (var r in B1Resource.All)
                if (r != null && r.Alive && r.ConstructionWord == word) matches++;
            if (matches != 1) throw new Exception("Expected one material for " + word + ", got " + matches);
            _waiting = true;
            Status = "Collecting/building " + word;
        }
        catch (Exception e) { End("FAIL: " + e); }
    }

    private static string Reverse(string word)
    {
        char[] letters = word.ToCharArray(); Array.Reverse(letters); return new string(letters);
    }

    private static void End(string message)
    {
        EditorApplication.update -= Tick;
        _scope?.Dispose(); _scope = null;
        Status = message;
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../output/stone-word-fix"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "verification.txt"), message);
        if (message.StartsWith("FAIL")) Debug.LogError(message); else Debug.Log(message);
    }
}
