using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class FootstepSounds : MonoBehaviour
{
    [SerializeField] SoundData footstep;
    [SerializeField] float stepDistance = 2f;
    [SerializeField] float maxVerticalSpeed = 1.5f;

    AudioSource source;
    Vector3 lastPosition;
    float distance;
    int lastIndex = -1;

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
        lastPosition = transform.position;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 delta = transform.position - lastPosition;
        lastPosition = transform.position;

        float verticalSpeed = Mathf.Abs(delta.y) / dt;
        delta.y = 0f;

        if (delta.magnitude / dt > 50f) return;
        if (verticalSpeed > maxVerticalSpeed) return;

        distance += delta.magnitude;
        if (distance < stepDistance) return;

        distance = 0f;
        Play();
    }

    void Play()
    {
        if (footstep == null || footstep.clips.Length == 0) return;

        int index = Random.Range(0, footstep.clips.Length);
        if (footstep.clips.Length > 1 && index == lastIndex)
            index = (index + 1) % footstep.clips.Length;
        lastIndex = index;

        source.clip = footstep.clips[index];
        source.volume = footstep.volume;
        source.pitch = Random.Range(footstep.pitchRange.x, footstep.pitchRange.y);
        source.Play();
    }
}
