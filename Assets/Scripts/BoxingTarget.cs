using UnityEngine;

/// <summary>
/// Marks the thing the boxer faces and circles around (a bag's mount, an opponent, or just an empty object).
/// Put it on any GameObject; Controller finds it automatically if its Target field is left empty.
/// Ranges are measured to the target's SURFACE: set <see cref="surfaceRadius"/> to the bag / opponent half-width.
/// </summary>
public class BoxingTarget : MonoBehaviour
{
    [Tooltip("Half-width of the bag or opponent (metres). Controller keeps its range from this surface, not the centre.")]
    [Min(0f)] public float surfaceRadius = 0.22f;

    [Tooltip("Editor-only: radius of the ring drawn around the target so you can see the circling area in the Scene view.")]
    [SerializeField] private float gizmoRadius = 1f;

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.9f);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 1f, 0.2f);
        DrawRing(surfaceRadius, new Color(1f, 0.6f, 0.2f, 0.9f));
        DrawRing(gizmoRadius, new Color(1f, 0.3f, 0.2f, 0.9f));
    }

    private void DrawRing(float radius, Color color)
    {
        if (radius <= 0f) return;
        Gizmos.color = color;
        const int segments = 32;
        Vector3 prev = transform.position + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float a = i / (float)segments * Mathf.PI * 2f;
            Vector3 next = transform.position + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
