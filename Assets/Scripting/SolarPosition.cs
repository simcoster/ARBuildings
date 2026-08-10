using UnityEngine;

public static class SolarPosition
{
    // Low-precision NOAA algorithm. Accurate to well under a degree — far better
    // than anything light estimation gives you outdoors.
    public static void Compute(double latDeg, double lonDeg, System.DateTime utc,
                               out float azimuthDeg, out float elevationDeg)
    {
        double d = (utc - new System.DateTime(2000, 1, 1, 12, 0, 0,
                    System.DateTimeKind.Utc)).TotalDays;

        double L = (280.460 + 0.9856474 * d) % 360.0;   // mean longitude
        double g = (357.528 + 0.9856003 * d) % 360.0;   // mean anomaly
        if (L < 0) L += 360;
        if (g < 0) g += 360;

        double gRad = g * Mathf.Deg2Rad;
        double lambda = (L + 1.915 * System.Math.Sin(gRad)
                           + 0.020 * System.Math.Sin(2 * gRad)) * Mathf.Deg2Rad;
        double eps = (23.439 - 0.0000004 * d) * Mathf.Deg2Rad;

        double ra = System.Math.Atan2(System.Math.Cos(eps) * System.Math.Sin(lambda),
                                       System.Math.Cos(lambda));
        double dec = System.Math.Asin(System.Math.Sin(eps) * System.Math.Sin(lambda));

        double gmst = (18.697374558 + 24.06570982441908 * d) % 24.0;
        if (gmst < 0) gmst += 24;
        double lmst = gmst * 15.0 + lonDeg;
        double ha = (lmst - ra * Mathf.Rad2Deg) * Mathf.Deg2Rad;

        double latRad = latDeg * Mathf.Deg2Rad;
        double el = System.Math.Asin(
            System.Math.Sin(latRad) * System.Math.Sin(dec) +
            System.Math.Cos(latRad) * System.Math.Cos(dec) * System.Math.Cos(ha));
        double az = System.Math.Atan2(
            -System.Math.Sin(ha),
            System.Math.Tan(dec) * System.Math.Cos(latRad) -
            System.Math.Sin(latRad) * System.Math.Cos(ha));

        azimuthDeg = (float)((az * Mathf.Rad2Deg + 360.0) % 360.0);
        elevationDeg = (float)(el * Mathf.Rad2Deg);
    }
}