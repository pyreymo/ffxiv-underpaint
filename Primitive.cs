using System.Numerics;

namespace Underpaint;

/// <summary>One primitive submitted as part of a render frame.</summary>
public readonly record struct Primitive(
    PrimitiveType Type,
    ulong Id,
    Matrix4x4 CurrentTransform,
    Matrix4x4 PreviousTransform,
    Vector3 Color,
    float Alpha,
    float DitherFade
);
