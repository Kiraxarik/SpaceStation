using Unity.Entities;
using UnityEngine;

/// <summary>
/// Simple runtime draw-distance slider. Put this on any GameObject in your
/// main scene. Adjusts all 4 radii together, proportionally, based on a
/// single 0–1 slider value mapped to a min/max VeryFarRadius range.
/// </summary>
public class DrawDistanceUI : MonoBehaviour
{
    public int MinVeryFarRadius = 16;
    public int MaxVeryFarRadius = 128;

    float _sliderValue = 0.5f; // start at midpoint

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(Screen.width - 320, 10, 300, 80));
        GUILayout.Label($"Draw Distance: {CurrentVeryFarRadius()} chunks");
        float newValue = GUILayout.HorizontalSlider(_sliderValue, 0f, 1f);
        GUILayout.EndArea();

        if (!Mathf.Approximately(newValue, _sliderValue))
        {
            _sliderValue = newValue;
            ApplyToSingleton();
        }
    }

    int CurrentVeryFarRadius()
        => Mathf.RoundToInt(Mathf.Lerp(MinVeryFarRadius, MaxVeryFarRadius, _sliderValue));

    void ApplyToSingleton()
    {
        foreach (var world in World.All)
        {
            if (world.Name != "ClientWorld") continue;

            var query = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadWrite<ChunkViewDistanceSettings>());
            if (query.IsEmpty) return;

            // Scale all four radii proportionally to the original default ratios,
            // anchored off the new VeryFarRadius.
            var defaults = ChunkViewDistanceSettings.Defaults;
            float scale = (float)CurrentVeryFarRadius() / defaults.VeryFarRadius;

            var settings = new ChunkViewDistanceSettings
            {
                FullDetailRadius = Mathf.Max(1, Mathf.RoundToInt(defaults.FullDetailRadius * scale)),
                MediumLODRadius = Mathf.Max(2, Mathf.RoundToInt(defaults.MediumLODRadius * scale)),
                FarLODRadius = Mathf.Max(3, Mathf.RoundToInt(defaults.FarLODRadius * scale)),
                VeryFarRadius = CurrentVeryFarRadius(),
            };

            query.SetSingleton(settings);
        }
    }
}