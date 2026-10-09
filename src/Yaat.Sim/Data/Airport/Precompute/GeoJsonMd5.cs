using System.Security.Cryptography;
using System.Text;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// The one definition of an airport GeoJSON's MD5, the <see cref="PrecomputeKey.GeoJsonMd5"/> a precompute entry is keyed
/// on: the lowercase hex MD5 of the GeoJSON text as fetched from vNAS, encoded UTF-8 without a byte-order mark. Everything
/// that keys an entry hashes through it, so the same map always gives the same key.
/// </summary>
public static class GeoJsonMd5
{
    /// <summary>The MD5 of <paramref name="geoJson"/>, lowercase hex.</summary>
    /// <param name="geoJson">The GeoJSON text.</param>
    /// <returns>32 lowercase hex digits.</returns>
    public static string Of(string geoJson)
    {
        ArgumentNullException.ThrowIfNull(geoJson);

        // A content fingerprint compared for equality with a stored one, not a security boundary.
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(geoJson))).ToLowerInvariant();
    }
}
