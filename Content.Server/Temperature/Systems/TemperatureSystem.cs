using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Alert;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Temperature.Components;
using Content.Shared.Administration.Logs;
using Content.Shared.Alert;
using Content.Shared.Damage;
using Content.Shared.Database;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Server.Temperature.Systems
{
    public class TemperatureSystem : EntitySystem
    {
        [Dependency] private readonly DamageableSystem _damageableSystem = default!;
        [Dependency] private readonly AtmosphereSystem _atmosphereSystem = default!;
        [Dependency] private readonly AdminLogSystem _logSystem = default!;

        /// <summary>
        ///     All the components that will have their damage updated at the end of the tick.
        ///     This is done because both AtmosExposed and Flammable call ChangeHeat in the same tick, meaning
        ///     that we need some mechanism to ensure it doesn't double dip on damage for both calls.
        /// </summary>
        public HashSet<TemperatureComponent> ShouldUpdateDamage = new();

        public float UpdateInterval = 1.0f;

        private float _accumulatedFrametime = 0.0f;

        public override void Initialize()
        {
            SubscribeLocalEvent<TemperatureComponent, OnTemperatureChangeEvent>(EnqueueDamage);
            SubscribeLocalEvent<TemperatureComponent, AtmosExposedUpdateEvent>(OnAtmosExposedUpdate);
            SubscribeLocalEvent<ServerAlertsComponent, OnTemperatureChangeEvent>(ServerAlert);
            SubscribeLocalEvent<TemperatureProtectionComponent, ModifyChangedTemperatureEvent>(OnTemperatureChangeAttempt);
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            _accumulatedFrametime += frameTime;

            if (_accumulatedFrametime < UpdateInterval)
                return;
            _accumulatedFrametime -= UpdateInterval;

            if (!ShouldUpdateDamage.Any())
                return;

            foreach (var comp in ShouldUpdateDamage)
            {
                if (comp.Deleted || comp.Paused)
                    continue;

                ChangeDamage((comp).Owner, comp);
            }

            ShouldUpdateDamage.Clear();
        }

        public void ForceChangeTemperature(EntityUid uid, float temp, TemperatureComponent? temperature = null)
        {
            if (Resolve(uid, ref temperature))
            {
                float lastTemp = temperature.CurrentTemperature;
                float delta = temperature.CurrentTemperature - temp;
                temperature.CurrentTemperature = temp;
                RaiseLocalEvent(uid, new OnTemperatureChangeEvent(temperature.CurrentTemperature, lastTemp, delta));
            }
        }

        public void ChangeHeat(EntityUid uid, float heatAmount, bool ignoreHeatResistance=false, TemperatureComponent? temperature = null)
        {
            if (Resolve(uid, ref temperature))
            {
                if (!ignoreHeatResistance)
                {
                    var ev = new ModifyChangedTemperatureEvent(heatAmount);
                    RaiseLocalEvent(uid, ev, false);
                    heatAmount = ev.TemperatureDelta;
                }

                float lastTemp = temperature.CurrentTemperature;
                temperature.CurrentTemperature += heatAmount / temperature.HeatCapacity;
                float delta = temperature.CurrentTemperature - lastTemp;

                RaiseLocalEvent(uid, new OnTemperatureChangeEvent(temperature.CurrentTemperature, lastTemp, delta));
            }
        }

        private void OnAtmosExposedUpdate(EntityUid uid, TemperatureComponent temperature, ref AtmosExposedUpdateEvent args)
        {
            var temperatureDelta = args.GasMixture.Temperature - temperature.CurrentTemperature;
            var tileHeatCapacity = _atmosphereSystem.GetTileHeatCapacity(args.Coordinates);
            var heat = temperatureDelta * (tileHeatCapacity * temperature.HeatCapacity / (tileHeatCapacity + temperature.HeatCapacity));
            ChangeHeat(uid, heat * temperature.AtmosTemperatureTransferEfficiency, temperature: temperature );
        }

        private void ServerAlert(EntityUid uid, ServerAlertsComponent status, OnTemperatureChangeEvent args)
        {
            switch (args.CurrentTemperature)
            {
                // Cold strong.
                case <= 260:
                    status.ShowAlert(AlertType.Cold, 3);
                    break;

                // Cold mild.
                case <= 280 and > 260:
                    status.ShowAlert(AlertType.Cold, 2);
                    break;

                // Cold weak.
                case <= 292 and > 280:
                    status.ShowAlert(AlertType.Cold, 1);
                    break;

                // Safe.
                case <= 327 and > 292:
                    status.ClearAlertCategory(AlertCategory.Temperature);
                    break;

                // Heat weak.
                case <= 335 and > 327:
                    status.ShowAlert(AlertType.Hot, 1);
                    break;

                // Heat mild.
                case <= 360 and > 335:
                    status.ShowAlert(AlertType.Hot, 2);
                    break;

                // Heat strong.
                case > 360:
                    status.ShowAlert(AlertType.Hot, 3);
                    break;
            }
        }

        private void EnqueueDamage(EntityUid uid, TemperatureComponent component, OnTemperatureChangeEvent args)
        {
            ShouldUpdateDamage.Add(component);
        }

        private void ChangeDamage(EntityUid uid, TemperatureComponent temperature)
        {
            if (!EntityManager.TryGetComponent<DamageableComponent>(uid, out var damage))
                return;

            // See this link for where the scaling func comes from:
            // https://www.desmos.com/calculator/0vknqtdvq9
            // Based on a logistic curve, which caps out at MaxDamage
            var heatK = 0.005;
            var a = 1;
            var y = temperature.DamageCap;
            var c = y * 2;

            if (temperature.CurrentTemperature >= temperature.HeatDamageThreshold)
            {
                if (!temperature.TakingDamage)
                {
                    _logSystem.Add(LogType.Temperature, $"{ToPrettyString(temperature.Owner):entity} started taking high temperature damage");
                    temperature.TakingDamage = true;
                }

                var diff = Math.Abs(temperature.CurrentTemperature - temperature.HeatDamageThreshold);
                var tempDamage = c / (1 + a * Math.Pow(Math.E, -heatK * diff)) - y;
                _damageableSystem.TryChangeDamage(uid, temperature.HeatDamage * tempDamage);
            }
            else if (temperature.CurrentTemperature <= temperature.ColdDamageThreshold)
            {
                if (!temperature.TakingDamage)
                {
                    _logSystem.Add(LogType.Temperature, $"{ToPrettyString(temperature.Owner):entity} started taking low temperature damage");
                    temperature.TakingDamage = true;
                }

                var diff = Math.Abs(temperature.CurrentTemperature - temperature.ColdDamageThreshold);
                var tempDamage =
                    Math.Sqrt(diff * (Math.Pow(temperature.DamageCap.Double(), 2) / temperature.ColdDamageThreshold));
                _damageableSystem.TryChangeDamage(uid, temperature.ColdDamage * tempDamage);
            }
            else if (temperature.TakingDamage)
            {
                _logSystem.Add(LogType.Temperature, $"{ToPrettyString(temperature.Owner):entity} stopped taking temperature damage");
                temperature.TakingDamage = false;
            }
        }

        private void OnTemperatureChangeAttempt(EntityUid uid, TemperatureProtectionComponent component, ModifyChangedTemperatureEvent args)
        {
            args.TemperatureDelta *= component.Coefficient;
        }
    }

    public class OnTemperatureChangeEvent : EntityEventArgs
    {
        public float CurrentTemperature { get; }
        public float LastTemperature { get; }
        public float TemperatureDelta { get; }

        public OnTemperatureChangeEvent(float current, float last, float delta)
        {
            CurrentTemperature = current;
            LastTemperature = last;
            TemperatureDelta = delta;
        }
    }

    public class ModifyChangedTemperatureEvent : EntityEventArgs
    {
        public float TemperatureDelta;

        public ModifyChangedTemperatureEvent(float temperature)
        {
            TemperatureDelta = temperature;
        }
    }
}
