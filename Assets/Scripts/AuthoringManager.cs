using UnityEngine;

public class AuthoringManager : MonoBehaviour
{
    [SerializeField] private Transform transformParent;
    [SerializeField] private Transform table;
    [SerializeField] private float moveValue = 0.01f;
    [SerializeField] private float scaleValue = 0.1f;

    public void IncreaseTransformX()
    {
        transformParent.position = new Vector3(transformParent.position.x + moveValue, transformParent.position.y, transformParent.position.z);
    }

    public void DecreaseTransformX()
    {
        transformParent.position = new Vector3(transformParent.position.x - moveValue, transformParent.position.y, transformParent.position.z);
    }

    public void IncreaseScaleX()
    {
        table.localScale = new Vector3(table.localScale.x + scaleValue, table.localScale.y, table.localScale.z);
    }

    public void DecreaseScaleX()
    {
        table.localScale = new Vector3(table.localScale.x - scaleValue, table.localScale.y, table.localScale.z);
    }

    public void IncreaseTransformY()
    {
        transformParent.position = new Vector3(transformParent.position.x, transformParent.position.y + moveValue, transformParent.position.z);
    }

    public void DecreaseTransformY()
    {
        transformParent.position = new Vector3(transformParent.position.x, transformParent.position.y - moveValue, transformParent.position.z);
    }

    public void IncreaseScaleY()
    {
        table.localScale = new Vector3(table.localScale.x, table.localScale.y + scaleValue, table.localScale.z);
    }

    public void DecreaseScaleY()
    {
        table.localScale = new Vector3(table.localScale.x, table.localScale.y - scaleValue, table.localScale.z);
    }

    public void IncreaseTransformZ()
    {
        transformParent.position = new Vector3(transformParent.position.x, transformParent.position.y, transformParent.position.z + moveValue);
    }

    public void DecreaseTransformZ()
    {
        transformParent.position = new Vector3(transformParent.position.x, transformParent.position.y, transformParent.position.z - moveValue);
    }

    public void IncreaseScaleZ()
    {
        table.localScale = new Vector3(table.localScale.x, table.localScale.y, table.localScale.z + scaleValue);
    }

    public void DecreaseScaleZ()
    {
        table.localScale = new Vector3(table.localScale.x, table.localScale.y, table.localScale.z - scaleValue);
    }

    public void ReloadScene()
    {
        string currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        UnityEngine.SceneManagement.SceneManager.LoadScene(currentSceneName);
    }
}
