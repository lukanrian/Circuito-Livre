using System;

namespace RafaelElectrical.Services
{
    public static class ShortCircuitEstimate
    {
        // shortcut: modelo resistivo trifásico sem transformadores; ampliar com impedâncias complexas antes de verificar outras topologias.
        public static double SourceImpedance(double lineVoltage, double sourceKa) =>
            double.IsFinite(lineVoltage) && lineVoltage > 0 && double.IsFinite(sourceKa) && sourceKa > 0
                ? lineVoltage / (Math.Sqrt(3) * sourceKa * 1000) : double.NaN;

        public static double CurrentKa(double lineVoltage, double impedance) =>
            double.IsFinite(lineVoltage) && lineVoltage > 0 && double.IsFinite(impedance) && impedance > 0
                ? lineVoltage / (Math.Sqrt(3) * impedance * 1000) : double.NaN;
    }
}
