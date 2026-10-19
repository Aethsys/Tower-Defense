using UnityEngine;
using UnityEngine.Audio;

[RequireComponent(typeof(EnemyHealth))]
public class EnemyHitSound : MonoBehaviour
{
    [SerializeField] SoundData hitSound;
    [SerializeField] AudioMixerGroup output;
    [SerializeField] float maxDistance = 20f;

    EnemyHealth health;
    AudioSource source;
    int lastHealth;
    int lastIndex = -1;

    void Awake()
    {
        health = GetComponent<EnemyHealth>();
        source = gameObject.AddComponent<AudioSource>();
        source.outputAudioMixerGroup = output;
        source.spatialBlend = 1f;
        source.maxDistance = maxDistance;
        source.playOnAwake = false;
    }

    void OnEnable()
    {
        lastHealth = health.MaxHealth;
        health.OnHealthChanged += HandleHealthChanged;
    }

    void OnDisable()
    {
        health.OnHealthChanged -= HandleHealthChanged;
    }

    void HandleHealthChanged(int current, int max)
    {
        bool tookDamage = current < lastHealth;
        lastHealth = current;
        if (!tookDamage || current <= 0) return;
        Play();
    }

    void Play()
    {
        if (hitSound == null || hitSound.clips.Length == 0) return;

        int index = Random.Range(0, hitSound.clips.Length);
        if (hitSound.clips.Length > 1 && index == lastIndex)
            index = (index + 1) % hitSound.clips.Length;
        lastIndex = index;

        source.pitch = Random.Range(hitSound.pitchRange.x, hitSound.pitchRange.y);
        source.PlayOneShot(hitSound.clips[index], hitSound.volume);
    }
}
