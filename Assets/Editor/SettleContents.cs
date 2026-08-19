using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Bakes a settled physics pose into the scene by selecting a container and running the
/// tool from the Tools menu.
/// </summary>
public static class SettleContents
{
    const float Step = 0.02f;
    const int Steps = 500; // 10 s

    [MenuItem("Tools/Settle Contents %#s")]
    static void Settle()
    {
        var bodies = new List<Rigidbody>();
        foreach (var go in Selection.gameObjects)
            bodies.AddRange(go.GetComponentsInChildren<Rigidbody>());

        if (bodies.Count == 0)
        {
            Debug.LogWarning("Settle Contents: select a container with Rigidbody children.");
            return;
        }

        var transforms = bodies.ConvertAll(rb => (Object)rb.transform).ToArray();
        Undo.RecordObjects(transforms, "Settle Contents");

        // Unstack overlapping bodies gently instead of launching them across the room
        foreach (var rb in bodies)
            rb.maxDepenetrationVelocity = 0.1f;

        var previousMode = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;
        for (int i = 0; i < Steps; i++)
            Physics.Simulate(Step);
        Physics.simulationMode = previousMode;

        foreach (var rb in bodies)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        EditorSceneManager.MarkAllScenesDirty();
        Debug.Log($"Settle Contents: baked {bodies.Count} bodies.");
    }
}
