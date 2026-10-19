using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class MovementLoopSound : MonoBehaviour
{
    [SerializeField] float maxVolume = 0.75f;
    [SerializeField] float minSpeed = 0.1f;
    [SerializeField] float fadeSpeed = 6f;

    AudioSource source;
    Vector3 lastPosition;

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.loop = true;
        source.playOnAwake = false;
        source.volume = 0f;
    }

    void OnEnable()
    {
        lastPosition = transform.position;
        if (source.clip == null) return;
        source.time = Random.Range(0f, source.clip.length);
        source.Play();
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 delta = transform.position - lastPosition;
        lastPosition = transform.position;
        delta.y = 0f;

        float target = delta.magnitude / dt > minSpeed ? maxVolume : 0f;
        source.volume = Mathf.MoveTowards(source.volume, target, fadeSpeed * maxVolume * dt);
    }
}
