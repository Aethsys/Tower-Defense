
using UnityEngine;

public class EnemyPath : MonoBehaviour
{
    [Header("Route Settings")]
    [SerializeField] private Transform[] waypoints;

    public int WaypointCount => waypoints.Length;

    public Transform GetWaypoint(int index)
    {
        if (index < 0 || index >= waypoints.Length)
            return null;

        return waypoints[index];
    }

    private void OnDrawGizmos()
    {
        if (waypoints == null || waypoints.Length == 0)
            return;

        Gizmos.color = Color.cyan;

        for (int i = 0; i < waypoints.Length; i++)
        {
            if (waypoints[i] == null)
                continue;

            Gizmos.DrawSphere(waypoints[i].position, 0.2f);

            if (i + 1 < waypoints.Length &&
                waypoints[i + 1] != null)
            {
                Gizmos.DrawLine(
                    waypoints[i].position,
                    waypoints[i + 1].position
                );
            }
        }
    }
}
