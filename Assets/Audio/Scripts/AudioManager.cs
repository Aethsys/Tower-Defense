using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

[System.Serializable]
public struct AmbienceLayer
{
    public AudioClip clip;
    [Range(0f, 1f)] public float volume;
}

public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [SerializeField] AudioMixerGroup sfxGroup, musicGroup, ambienceGroup;
    [SerializeField] AudioSource musicSource;
    [SerializeField] int poolSize = 12;

    AudioSource[] pool;
    readonly List<AudioSource> ambienceSources = new List<AudioSource>();
    int next;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        transform.SetParent(null);
        DontDestroyOnLoad(gameObject);

        pool = new AudioSource[poolSize];
        for (int i = 0; i < poolSize; i++)
        {
            pool[i] = gameObject.AddComponent<AudioSource>();
            pool[i].outputAudioMixerGroup = sfxGroup;
            pool[i].playOnAwake = false;
        }
        musicSource.outputAudioMixerGroup = musicGroup;
    }

    public void PlaySFX(SoundData s)
    {
        if (s == null || s.clips.Length == 0) return;
        var src = pool[next];
        next = (next + 1) % pool.Length;
        src.clip = s.clips[Random.Range(0, s.clips.Length)];
        src.volume = s.volume;
        src.pitch = Random.Range(s.pitchRange.x, s.pitchRange.y);
        src.Play();
    }

    public void PlayMusic(AudioClip clip)
    {
        if (musicSource.clip == clip) return;
        musicSource.clip = clip;
        musicSource.loop = true;
        musicSource.Play();
    }

    public void PlayAmbience(AmbienceLayer[] layers)
    {
        if (layers == null || layers.Length == 0)
        {
            StopAmbience();
            return;
        }

        foreach (var src in ambienceSources)
        {
            if (src.clip != null && !ContainsClip(layers, src.clip))
            {
                src.Stop();
                src.clip = null;
            }
        }

        foreach (var layer in layers)
        {
            if (layer.clip == null) continue;

            var src = FindAmbienceSource(layer.clip);
            if (src == null)
            {
                src = GetFreeAmbienceSource();
                src.clip = layer.clip;
                src.Play();
            }
            src.volume = layer.volume;
        }
    }

    public void StopAmbience()
    {
        foreach (var src in ambienceSources)
        {
            src.Stop();
            src.clip = null;
        }
    }

    static bool ContainsClip(AmbienceLayer[] layers, AudioClip clip)
    {
        foreach (var layer in layers)
            if (layer.clip == clip) return true;
        return false;
    }

    AudioSource FindAmbienceSource(AudioClip clip)
    {
        foreach (var src in ambienceSources)
            if (src.clip == clip) return src;
        return null;
    }

    AudioSource GetFreeAmbienceSource()
    {
        foreach (var src in ambienceSources)
            if (src.clip == null) return src;

        var created = gameObject.AddComponent<AudioSource>();
        created.outputAudioMixerGroup = ambienceGroup;
        created.playOnAwake = false;
        created.loop = true;
        ambienceSources.Add(created);
        return created;
    }
}
