namespace VisioView.Core;

/// <summary>
/// 2x3 仿射变换 (a b c d e f):
/// x' = a*x + c*y + e
/// y' = b*x + d*y + f
/// 用于把 Visio 的本地坐标逐级映射到页面坐标。
/// </summary>
public readonly struct Affine
{
    public readonly double A, B, C, D, E, F;

    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public Affine(double a, double b, double c, double d, double e, double f)
    {
        A = a; B = b; C = c; D = d; E = e; F = f;
    }

    public (double X, double Y) Apply(double x, double y)
        => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>组合 outer∘inner：先应用 inner 再应用 outer。</summary>
    public static Affine Compose(Affine outer, Affine inner)
    {
        return new Affine(
            outer.A * inner.A + outer.C * inner.B,
            outer.B * inner.A + outer.D * inner.B,
            outer.A * inner.C + outer.C * inner.D,
            outer.B * inner.C + outer.D * inner.D,
            outer.A * inner.E + outer.C * inner.F + outer.E,
            outer.B * inner.E + outer.D * inner.F + outer.F);
    }

    /// <summary>
    /// 由 Visio 形状放置参数构造「本地坐标 -> 父坐标」的变换。
    /// sx/sy 为相对源几何自然尺寸的缩放（母版实例/缩放时使用，普通形状为 1）。
    /// </summary>
    public static Affine MakePlacement(
        double pinX, double pinY, double locPinX, double locPinY,
        double angle, double sx, double sy, double flipX = 1, double flipY = 1)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double a = cos * sx * flipX;
        double b = sin * sx * flipX;
        double c = -sin * sy * flipY;
        double d = cos * sy * flipY;
        double e = pinX - cos * flipX * locPinX + sin * flipY * locPinY;
        double f = pinY - sin * flipX * locPinX - cos * flipY * locPinY;
        return new Affine(a, b, c, d, e, f);
    }
}
