using System.Collections;
using UnityEngine;
using UnityEngine.Audio;

public class RandomAmbience : MonoBehaviour
{
    [SerializeField] SoundData[] sounds;
    [SerializeField] AudioMixerGroup output;
    [SerializeField] Vector2 interval = new Vector2(5f, 15f);
    [SerializeField] float radius = 20f;
    [SerializeField] Transform center;

    AudioSource source;

    void Awake()
    {
        source = gameObject.AddComponent<AudioSource>();
        source.outputAudioMixerGroup = output;
        source.spatialBlend = 1f;
        source.playOnAwake = false;
    }

    void OnEnable() => StartCoroutine(Loop());

    IEnumerator Loop()
    {
        while (true)
        {
            yield return new WaitForSeconds(Random.Range(interval.x, interval.y));
            if (sounds.Length == 0) continue;

            var s = sounds[Random.Range(0, sounds.Length)];
            Vector3 origin = center != null ? center.position : transform.position;
            Vector2 offset = Random.insideUnitCircle * radius;
            transform.position = origin + new Vector3(offset.x, 0f, offset.y);

            source.clip = s.clips[Random.Range(0, s.clips.Length)];
            source.volume = s.volume;
            source.pitch = Random.Range(s.pitchRange.x, s.pitchRange.y);
            source.Play();
        }
    }
}
