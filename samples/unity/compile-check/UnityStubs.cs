// Minimal stand-ins for the UnityEngine types BonfireExample.cs uses, so the sample can be
// compile-checked against the netstandard2.0 Bonfire build without installing Unity.
// They do nothing at runtime. Do not ship them.
#pragma warning disable CS1591
namespace UnityEngine
{
    public class Object
    {
        public string name = "";
        public static T Instantiate<T>(T original) where T : Object => original;
        public static void Destroy(Object obj) { }
    }
    public class Component : Object { public Transform transform = new Transform(); }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public class Transform : Component { public Vector3 position; public bool hasChanged; }
    public class GameObject : Object
    {
        public Transform transform = new Transform();
        public static GameObject CreatePrimitive(PrimitiveType type) => new GameObject();
    }
    public enum PrimitiveType { Sphere, Capsule, Cylinder, Cube, Plane, Quad }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
    }
    public static class Application { public static string persistentDataPath => "."; }
    public static class Time { public static float time => 0; }
    [System.AttributeUsage(System.AttributeTargets.Field)] public sealed class SerializeField : System.Attribute { }
    [System.AttributeUsage(System.AttributeTargets.Field)] public sealed class TooltipAttribute : System.Attribute { public TooltipAttribute(string t) { } }
}
