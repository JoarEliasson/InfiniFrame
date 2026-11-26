using System.Drawing;
using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>
///     The protocol-3 window style fields of Create: frame, user resizing, the bounds reference and size limits. The flag
///     values mirror <c>kWindow*</c> in the companion's protocol.h. A size of 0 leaves that dimension unconstrained.
/// </summary>
internal readonly record struct CefWindowStyle(bool Frameless, bool FixedSize, bool ContentBounds, Size Minimum, Size Maximum) {
    internal const uint FramelessFlag = 1, FixedSizeFlag = 2, ContentBoundsFlag = 4;

    /// <summary>A framed, user-resizable window measured by its outer bounds, without size limits (protocol 2 behaviour).</summary>
    internal static CefWindowStyle Default => new(false, false, false, Size.Empty, Size.Empty);

    internal uint Flags => (Frameless ? FramelessFlag : 0) | (FixedSize ? FixedSizeFlag : 0) | (ContentBounds ? ContentBoundsFlag : 0);

    /// <summary>
    ///     The style a window request asks for (host contract 1.1). Sizes stay outer window sizes, which for a chromeless
    ///     window equal the content area. A zero minimum dimension and an <see cref="int.MaxValue" /> maximum dimension
    ///     are unconstrained; a maximum beyond the companion's 16384 bounds limit cannot constrain anything either.
    /// </summary>
    internal static CefWindowStyle From(InfiniFrameHostWindowRequest request) =>
        new(request.Chromeless, !request.Resizable, false,
            request.MinimumSize is {} minimum ? new Size(minimum.Width, minimum.Height) : Size.Empty,
            request.MaximumSize is {} maximum ? new Size(Unbounded(maximum.Width), Unbounded(maximum.Height)) : Size.Empty);

    internal const int MaximumDimension = 16384;

    private static int Unbounded(int maximum) => maximum > MaximumDimension ? 0 : maximum;

    /// <summary>A size clamped into the limits, maximum first, then minimum (as InfiniFrame clamps).</summary>
    internal Size Clamp(Size size) {
        int width = Maximum.Width > 0 ? Math.Min(size.Width, Maximum.Width) : size.Width;
        int height = Maximum.Height > 0 ? Math.Min(size.Height, Maximum.Height) : size.Height;
        return new Size(Math.Max(width, Minimum.Width), Math.Max(height, Minimum.Height));
    }

    /// <summary>Limits the companion cannot represent or an initial size outside them; InfiniFrame clamps before calling.</summary>
    internal IEnumerable<string> Violations(Size initial) {
        if (Minimum.Width is < 0 or > MaximumDimension || Minimum.Height is < 0 or > MaximumDimension)
            yield return $"minimum size {Minimum.Width}x{Minimum.Height} (each dimension 0..{MaximumDimension})";
        else if (Maximum.Width < 0 || Maximum.Height < 0 || (Maximum.Width != 0 && Minimum.Width > Maximum.Width)
                 || (Maximum.Height != 0 && Minimum.Height > Maximum.Height))
            yield return $"size constraints {Minimum.Width}x{Minimum.Height}..{Maximum.Width}x{Maximum.Height}";
        else if (initial.Width < Minimum.Width || initial.Height < Minimum.Height
                 || (Maximum.Width != 0 && initial.Width > Maximum.Width) || (Maximum.Height != 0 && initial.Height > Maximum.Height))
            yield return $"initial size {initial.Width}x{initial.Height} outside its size constraints";
    }

    internal void Write(BinaryWriter writer) {
        writer.Write(Flags);
        writer.Write(Minimum.Width);
        writer.Write(Minimum.Height);
        writer.Write(Maximum.Width);
        writer.Write(Maximum.Height);
    }
}
