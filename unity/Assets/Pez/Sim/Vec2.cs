using System;

namespace Pez.Sim
{
    /// <summary>2D vector in tile units. Sim-space x maps to world X, y maps to world Z.</summary>
    public struct Vec2
    {
        public float X, Y;
        public Vec2(float x, float y) { X = x; Y = y; }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator /(Vec2 a, float s) => new Vec2(a.X / s, a.Y / s);

        public float Length => MathF.Sqrt(X * X + Y * Y);
        public float LengthSq => X * X + Y * Y;
        public Vec2 Normalized { get { var l = Length; return l > 1e-5f ? this / l : new Vec2(0, 0); } }

        public static float Dist(Vec2 a, Vec2 b) => (a - b).Length;
        public static float DistSq(Vec2 a, Vec2 b) => (a - b).LengthSq;
        public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => a + (b - a) * t;

        /// <summary>Heading in radians, 0 = +x, counter-clockwise toward +y.</summary>
        public float Angle => MathF.Atan2(Y, X);

        public override string ToString() => $"({X:0.0},{Y:0.0})";
    }

    public struct Int2 : IEquatable<Int2>
    {
        public int X, Y;
        public Int2(int x, int y) { X = x; Y = y; }
        public bool Equals(Int2 o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is Int2 o && Equals(o);
        public override int GetHashCode() => X * 73856093 ^ Y * 19349663;
        public Vec2 Center => new Vec2(X + 0.5f, Y + 0.5f);
        public static Int2 Of(Vec2 v) => new Int2((int)MathF.Floor(v.X), (int)MathF.Floor(v.Y));
        public override string ToString() => $"[{X},{Y}]";
    }
}
