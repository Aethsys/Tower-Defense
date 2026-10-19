using System.Collections.Generic;
using UnityEngine;

public class EnemyPath : MonoBehaviour
{
    [Header("Route Settings")]
    [SerializeField] private Transform[] waypoints;

    public int WaypointCount => waypoints != null ? waypoints.Length : 0;

    private void Awake()
    {
        useGUILayout = false;

        if (waypoints == null || waypoints.Length == 0)
        {
            AutoAssignChildWaypoints();
        }
    }

    private void Reset()
    {
        AutoAssignChildWaypoints();
    }

    public void AutoAssignChildWaypoints()
    {
        int childCount = transform.childCount;
        if (childCount == 0) return;

        List<Transform> list = new List<Transform>(childCount);
        for (int i = 0; i < childCount; i++)
        {
            Transform child = transform.GetChild(i);
            if (child != null)
            {
                list.Add(child);
            }
        }

        if (list.Count > 0)
        {
            waypoints = list.ToArray();
        }
    }

    public Transform GetWaypoint(int index)
    {
        if (waypoints == null || (uint)index >= (uint)waypoints.Length)
        {
            return null;
        }

        return waypoints[index];
    }

    public bool TryGetWaypointPosition(int index, out Vector3 position)
    {
        if (waypoints != null && (uint)index < (uint)waypoints.Length)
        {
            Transform wp = waypoints[index];
            if (wp != null)
            {
                position = wp.position;
                return true;
            }
        }

        position = default;
        return false;
    }

    public int GetClosestWaypointIndex(Vector3 currentPosition)
    {
        if (waypoints == null || waypoints.Length == 0) return 0;

        int closest = 0;
        float minSqrDist = float.MaxValue;

        for (int i = 0; i < waypoints.Length; i++)
        {
            Transform wp = waypoints[i];
            if (wp == null) continue;

            float sqrDist = (wp.position - currentPosition).sqrMagnitude;
            if (sqrDist < minSqrDist)
            {
                minSqrDist = sqrDist;
                closest = i;
            }
        }

        return closest;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (waypoints == null || waypoints.Length == 0)
        {
            return;
        }

        Color startColor = new Color(0.2f, 0.9f, 0.3f, 0.85f);
        Color endColor = new Color(1f, 0.2f, 0.3f, 0.85f);

        for (int i = 0; i < waypoints.Length; i++)
        {
            Transform current = waypoints[i];
            if (current == null) continue;

            float t = (float)i / Mathf.Max(1, waypoints.Length - 1);
            Gizmos.color = Color.Lerp(startColor, endColor, t);

            Vector3 currentPos = current.position;
            Gizmos.DrawSphere(currentPos, 0.25f);

            if (i + 1 < waypoints.Length)
            {
                Transform next = waypoints[i + 1];
                if (next != null)
                {
                    Gizmos.DrawLine(currentPos, next.position);

                    // Pequeña flecha direccional a mitad de tramo
                    Vector3 mid = (currentPos + next.position) * 0.5f;
                    Vector3 dir = (next.position - currentPos).normalized;
                    Vector3 right = Vector3.Cross(Vector3.up, dir) * 0.2f;
                    Gizmos.DrawLine(mid + dir * 0.25f, mid - dir * 0.2f + right);
                    Gizmos.DrawLine(mid + dir * 0.25f, mid - dir * 0.2f - right);
                }
            }
        }
    }
#endif
}
