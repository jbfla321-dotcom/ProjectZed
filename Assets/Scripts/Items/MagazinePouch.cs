using System.Collections.Generic;
using UnityEngine;

// The player's spare magazines, each with its own round count (DayZ style: rounds aren't pooled).
// Reloading takes the fullest one; the magazine coming out of the rifle is dropped on the ground.
public class MagazinePouch : MonoBehaviour
{
    [Tooltip("Spare full magazines at the start.")]
    public int startingMagazines = 0;
    public int magazineSize = 30;

    readonly List<int> magazines = new List<int>();

    public int Count => magazines.Count;
    public IReadOnlyList<int> Magazines => magazines;

    void Awake()
    {
        for (int i = 0; i < startingMagazines; i++) magazines.Add(magazineSize);
    }

    public void Add(int rounds) => magazines.Add(Mathf.Clamp(rounds, 0, magazineSize));

    // Removes and returns the fullest magazine that has any rounds, or -1 if there isn't one.
    public int TakeFullest()
    {
        int best = -1;
        for (int i = 0; i < magazines.Count; i++)
            if (magazines[i] > 0 && (best < 0 || magazines[i] > magazines[best])) best = i;
        if (best < 0) return -1;
        int rounds = magazines[best];
        magazines.RemoveAt(best);
        return rounds;
    }
}
