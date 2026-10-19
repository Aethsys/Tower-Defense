using UnityEngine;
using UnityEngine.Audio;

public class SceneMusic : MonoBehaviour
{
    [SerializeField] AudioClip music;
    [SerializeField] AmbienceLayer[] ambience;
    [SerializeField] AudioMixerSnapshot snapshot;
    [SerializeField] float transitionTime = 1.5f;

    void Start()
    {
        if (AudioManager.Instance == null) return;

        AudioManager.Instance.PlayMusic(music);
        AudioManager.Instance.PlayAmbience(ambience);

        if (snapshot != null) snapshot.TransitionTo(transitionTime);
    }
}
