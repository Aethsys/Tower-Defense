using UnityEngine;

[CreateAssetMenu(menuName = "Audio/Sound")]
public class SoundData : ScriptableObject
{
    public AudioClip[] clips;
    [Range(0f, 1f)] public float volume = 1f;
    public Vector2 pitchRange = new Vector2(0.95f, 1.05f);
}
