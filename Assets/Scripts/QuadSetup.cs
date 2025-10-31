using UnityEngine;

public class QuadSetup : MonoBehaviour
{
    public Camera mainCamera;
    public Transform quad_transform;
    public float z;

    void Start()
    {
        float vFOV = mainCamera.fieldOfView; // 垂直FOV (Unity Camera)
        float aspect = mainCamera.aspect;    // 横縦比

        // 水平FOVも計算可能
        float hFOV = 2f * Mathf.Atan(Mathf.Tan(vFOV * Mathf.Deg2Rad / 2f) * aspect) * Mathf.Rad2Deg;

        // Quadの幅と高さを計算
        float height = 2f * z * Mathf.Tan(vFOV * Mathf.Deg2Rad / 2f);
        float width = 2f * z * Mathf.Tan(hFOV * Mathf.Deg2Rad / 2f);

        // Quadのサイズを設定
        quad_transform.localPosition = new Vector3(0f, 0f, z);
        quad_transform.localScale = new Vector3(width, height, 1f);
    
    }
}
