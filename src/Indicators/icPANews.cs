// =========================================================================================
// Tool Name:    icPA - News
// Class:        icPANews
// Category:     Indicator
// Version:      1.5.4
// Platform:     NinjaTrader 8.0+
// Author:       Istvan Cebrian
// Brand:        icPriceAction
// Website:      https://icpriceaction.com
// Repository:   https://github.com/icPriceAction/news
// YouTube:      https://youtube.com/@icPriceAction
// Support:      https://buymeacoffee.com/icpriceaction
//
// License:      Free for personal trading use. Commercial resale, redistribution,
//               or repackaging is strictly prohibited. See LICENSE in official repository.
//
// Description:  Economic calendar on the chart from the Forex Factory weekly feed: a panel
//               with countdowns, vertical event markers, shaded news windows and optional
//               alerts, plus a real-time IsInNewsWindow flag for strategies.
//
// Changelog:
//   v1.5.4 - Initial public release. Cleaned and optimized by icPriceAction.
//          - Renamed to icPANews (namespace NinjaTrader.NinjaScript.Indicators.icPA),
//             display name "icPA - News".
//          - Upcoming events are placed where the chart's own time axis shows their
//            time, on time-based and Tick / Range / Renko / Volume charts alike.
//          - Upcoming events beyond the chart's right edge (within HUD Lookforward
//             Minutes) are pinned to the right edge with their time and countdown.
//          - Collapsing the panel hides the event list; the header line shows the
//            next event's time and countdown.
//          - Fixed click-to-collapse not responding while the panel sits over price bars.
//          - SharpDX brushes, stroke and text formats are created in
//            OnRenderTargetChanged instead of OnRender.
//          - Removed LINQ and per-frame list allocations from the render path.
//          - Default WPF brushes are frozen; the refresh timer is stopped on the UI thread.
//   v1.4.2 - Strategy API answers in real time only; no countdown for all-day
//            events; atomic disk cache write; collapsed empty panel divider fix.
//   v1.4.1 - Reliable click-to-collapse; empty collapsed panel shrinks to its header.
//   v1.4.0 - Time format (24h / 12h), day labels, dimmed past events,
//            click-to-collapse. Defaults: news window 5 / 5 min, alerts off.
//   v1.3.0 - Impact toggles, bank holidays, shaded news zones, alerts and the
//            strategy API. Defaults: lookback 120, lookforward 240, font 16.
//   v1.2.1 - Panel drawn above drawing objects; indicator label hidden by
//            default; HUD offset settings.
//   v1.2.0 - Themes (Auto, Dark, Light, Custom); markers can cover the entire week.
//   v1.1.0 - Built-in JSON parser, chart-type aware markers, Currencies setting,
//            all-day events, font-scaled panel, no network use outside charts
//            and live strategies.
//   v1.0.0 - Initial versions (1.0.x).
// =========================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.icPA
{
    public enum icPANewsTheme
    {
        Auto,
        Dark,
        Light,
        Custom
    }

    public enum icPANewsLineRange
    {
        EntireWeek,
        SameWindowAsHud
    }

    public enum icPANewsTimeFormat
    {
        TwentyFourHour,
        TwelveHour
    }

    public class icPANewsEventItem
    {
        public string Title { get; set; }
        public string Country { get; set; }
        public DateTime UtcDateTime { get; set; }
        public DateTime FeedDate { get; set; }   // calendar date in the feed's own time zone (used for all-day events)
        public bool IsAllDay { get; set; }
        public string Impact { get; set; }
        public string Forecast { get; set; }
        public string Previous { get; set; }
    }

    // -------------------------------------------------------------------------------------
    //  USAGE NOTES
    //  - The Forex Factory feed covers the current week only and has no history.
    //  - Times are shown in the time zone set in NinjaTrader (Tools > Options > General).
    //  - Past events are placed on the bar where they occurred. Upcoming events are placed where
    //    the chart's own time axis shows their time. On non-time-based charts (Tick, Range, Renko,
    //    Volume, ...) that future axis is NinjaTrader's estimate, so the line shifts as the pace
    //    of new bars changes.
    //  - Upcoming events that fall beyond the chart's right edge, and are within HUD
    //    Lookforward Minutes, are pinned to the right edge with their time and countdown.
    //  - Daily and higher charts show the panel only.
    //  - The calendar is downloaded at most once an hour and shared by all charts, with a
    //    disk cache in the NinjaTrader user folder (icPANews_FF_Cache.json).
    //  - Events the feed publishes at exactly midnight (New York time) are treated as
    //    all-day / tentative.
    //  - Click the panel to collapse it to one header line with the next event's countdown;
    //    click again to expand.
    //
    //  STRATEGY USAGE (real time only: in backtests IsInNewsWindow is always false)
    //    using NinjaTrader.NinjaScript.Indicators.icPA;
    //    private icPANews news;
    //    // State.DataLoaded:
    //    news = icPANews();
    //    news.NewsWindowMinutesBefore = 5;
    //    news.NewsWindowMinutesAfter  = 10;
    //    // OnBarUpdate:
    //    if (news.IsInNewsWindow)
    //        return;   // skip new entries around news
    // -------------------------------------------------------------------------------------
    [TypeConverter("NinjaTrader.NinjaScript.Indicators.icPA.icPANewsTypeConverter")]
    public class icPANews : Indicator
    {
        public const string IndicatorVersion = "1.5.4";

        #region Static Shared Fields (Across all chart instances & refreshes)
        private static readonly HttpClient httpClient = CreateHttpClient();
        private const string FeedUrl = "https://nfs.faireconomy.media/ff_calendar_thisweek.json";
        private const string CacheFileName = "icPANews_FF_Cache.json";
        private const int CacheMinutes = 60;
        private const int RateLimitBackoffMinutes = 20;
        private const int ErrorBackoffMinutes = 5;

        private static readonly List<icPANewsEventItem> sharedCachedEvents = new List<icPANewsEventItem>();
        private static readonly object sharedLock = new object();
        private static readonly SemaphoreSlim downloadSemaphore = new SemaphoreSlim(1, 1);
        private static DateTime sharedLastDownloadUtc = DateTime.MinValue;
        private static DateTime sharedNextAttemptUtc = DateTime.MinValue;   // backoff after 429 / network errors
        private static bool isRateLimited = false;                          // guarded by sharedLock

        // Events already alerted this session, shared so several charts don't alert twice (guarded by sharedLock)
        private static readonly HashSet<string> sharedAlertedKeys = new HashSet<string>();

        // Currencies that appear in the Forex Factory feed
        private static readonly HashSet<string> FeedCurrencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "USD", "EUR", "GBP", "JPY", "AUD", "NZD", "CAD", "CHF", "CNY"
        };

        // Futures master instrument names -> relevant currencies
        private static readonly Dictionary<string, string[]> FuturesCurrencyMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // CME FX futures: standard, E-mini and micro
            { "6E",  new[] { "EUR", "USD" } }, { "E7",  new[] { "EUR", "USD" } }, { "M6E", new[] { "EUR", "USD" } },
            { "6B",  new[] { "GBP", "USD" } }, { "M6B", new[] { "GBP", "USD" } },
            { "6J",  new[] { "JPY", "USD" } }, { "J7",  new[] { "JPY", "USD" } }, { "MJY", new[] { "JPY", "USD" } },
            { "6A",  new[] { "AUD", "USD" } }, { "M6A", new[] { "AUD", "USD" } },
            { "6C",  new[] { "CAD", "USD" } }, { "MCD", new[] { "CAD", "USD" } },
            { "6S",  new[] { "CHF", "USD" } }, { "MSF", new[] { "CHF", "USD" } },
            { "6N",  new[] { "NZD", "USD" } },

            // Eurex index and bond futures
            { "FDAX", new[] { "EUR" } }, { "FDXM", new[] { "EUR" } }, { "FDXS", new[] { "EUR" } },
            { "FESX", new[] { "EUR" } }, { "FSXE", new[] { "EUR" } },
            { "FGBL", new[] { "EUR" } }, { "FGBM", new[] { "EUR" } }, { "FGBS", new[] { "EUR" } }, { "FGBX", new[] { "EUR" } },

            // UK
            { "Z", new[] { "GBP" } },

            // Nikkei futures traded in the US
            { "NKD", new[] { "JPY", "USD" } }, { "NIY", new[] { "JPY", "USD" } }
        };
        #endregion

        #region Theme Palettes
        private struct ThemeColors
        {
            public System.Windows.Media.Color Background;
            public System.Windows.Media.Color Border;
            public System.Windows.Media.Color Text;
            public System.Windows.Media.Color DimText;
            public System.Windows.Media.Color HighImpact;
            public System.Windows.Media.Color MedImpact;
        }

        private static readonly ThemeColors DarkPalette = new ThemeColors
        {
            Background = System.Windows.Media.Color.FromRgb(20, 24, 30),
            Border     = System.Windows.Media.Color.FromRgb(70, 75, 85),
            Text       = System.Windows.Media.Color.FromRgb(242, 242, 242),
            DimText    = System.Windows.Media.Color.FromRgb(166, 173, 184),
            HighImpact = System.Windows.Media.Color.FromRgb(230, 40, 40),
            MedImpact  = System.Windows.Media.Color.FromRgb(240, 150, 20)
        };

        private static readonly ThemeColors LightPalette = new ThemeColors
        {
            Background = System.Windows.Media.Color.FromRgb(250, 250, 252),
            Border     = System.Windows.Media.Color.FromRgb(190, 195, 205),
            Text       = System.Windows.Media.Color.FromRgb(25, 28, 34),
            DimText    = System.Windows.Media.Color.FromRgb(100, 106, 118),
            HighImpact = System.Windows.Media.Color.FromRgb(196, 26, 26),
            MedImpact  = System.Windows.Media.Color.FromRgb(196, 105, 0)
        };
        #endregion

        #region Private Instance Fields
        private readonly List<icPANewsEventItem> hudEvents = new List<icPANewsEventItem>();
        private readonly List<icPANewsEventItem> lineEvents = new List<icPANewsEventItem>();
        private readonly object instanceLock = new object();

        private readonly HashSet<string> targetCurrencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string currencyHeader = "USD";
        private string headerTextExpanded;
        private string headerTextCollapsed;
        private System.Windows.Threading.DispatcherTimer updateTimer;
        private DateTime lastHostRefreshUtc = DateTime.MinValue;   // strategy-hosted refresh throttle

        // HUD click-to-collapse
        private System.Windows.UIElement mouseSubscribedControl;
        private System.Windows.Input.MouseButtonEventHandler mouseDownHandler;
        private int lastToggleTimestamp = int.MinValue;
        private SharpDX.RectangleF lastHudRect;
        private bool hasHudRect;

        // Chart type, resolved in State.DataLoaded
        private bool timeBasedChart = true;
        private bool dailyOrHigherChart = false;

        // Bars context for non-time-based charts, prepared at the start of each render
        private NinjaTrader.Data.Bars renderBars;
        private DateTime renderFirstBarTime;
        private DateTime renderLastBarTime;

        // Forward projection on non-time-based charts (upcoming events beyond the last bar)
        private const int ProjectionSampleBars = 50;
        private readonly double[] barIntervalBuffer = new double[ProjectionSampleBars];
        private int projectionBarsCount = -1;
        private double renderSecondsPerBar;
        private float renderLastBarX;
        private float renderBarDistance;
        private float renderPanelRight;   // right edge of the price panel, for the time-axis search

        // Reusable render buffers (UI thread only) so OnRender does not allocate lists every frame
        private readonly List<icPANewsEventItem> renderHudList = new List<icPANewsEventItem>();
        private readonly List<icPANewsEventItem> renderLineList = new List<icPANewsEventItem>();
        private readonly List<MarkerItem> renderVisible = new List<MarkerItem>();
        private readonly Dictionary<int, int> renderTopRankByX = new Dictionary<int, int>();
        private readonly List<SharpDX.RectangleF> renderPlacedLabels = new List<SharpDX.RectangleF>();
        private readonly List<HudRow> renderRows = new List<HudRow>();

        // Theme the current SharpDX resources were built for (Dark, Light or Custom; never Auto)
        private icPANewsTheme resolvedTheme = icPANewsTheme.Dark;

        // Text measurement cache (UI thread only), cleared whenever the text formats are rebuilt
        private readonly Dictionary<SharpDX.DirectWrite.TextFormat, Dictionary<string, float>> textWidthCache =
            new Dictionary<SharpDX.DirectWrite.TextFormat, Dictionary<string, float>>();
        private readonly Dictionary<SharpDX.DirectWrite.TextFormat, float> textHeightCache =
            new Dictionary<SharpDX.DirectWrite.TextFormat, float>();

        // SharpDX Resources
        private SharpDX.Direct2D1.SolidColorBrush sharpDxHudBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush sharpDxHudBorderBrush;
        private SharpDX.Direct2D1.SolidColorBrush sharpDxHighImpactBrush;
        private SharpDX.Direct2D1.SolidColorBrush sharpDxMedImpactBrush;
        private SharpDX.Direct2D1.SolidColorBrush sharpDxTextBrush;
        private SharpDX.Direct2D1.SolidColorBrush sharpDxDimTextBrush;
        private SharpDX.Direct2D1.SolidColorBrush sharpDxHighZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush sharpDxMedZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush sharpDxLowZoneBrush;
        private SharpDX.Direct2D1.StrokeStyle sharpDxStrokeStyle;
        private SharpDX.DirectWrite.TextFormat sharpDxTextFormat;
        private SharpDX.DirectWrite.TextFormat sharpDxBoldFormat;
        private SharpDX.DirectWrite.TextFormat sharpDxSmallFormat;
        #endregion

        #region Properties - 1. Filters
        [Display(Name = "High Impact", Description = "Show high impact events", Order = 1, GroupName = "1. Filters")]
        public bool ShowHighImpact { get; set; }

        [Display(Name = "Medium Impact", Description = "Show medium impact events", Order = 2, GroupName = "1. Filters")]
        public bool ShowMediumImpact { get; set; }

        [Display(Name = "Low Impact", Description = "Show low impact events", Order = 3, GroupName = "1. Filters")]
        public bool ShowLowImpact { get; set; }

        [Display(Name = "Bank Holidays", Description = "Show bank holidays (thin liquidity days)", Order = 4, GroupName = "1. Filters")]
        public bool ShowHolidays { get; set; }

        [Display(Name = "Currencies", Description = "Auto = detect from the instrument. All = every currency. Or a comma-separated list, e.g. USD,EUR,CNY", Order = 5, GroupName = "1. Filters")]
        public string Currencies { get; set; }

        [Range(0, 1440)]
        [Display(Name = "HUD Lookback Minutes", Description = "Past events older than this are removed from the HUD", Order = 6, GroupName = "1. Filters")]
        public int LookbackMinutes { get; set; }

        [Range(1, 1440)]
        [Display(Name = "HUD Lookforward Minutes", Description = "Events further ahead than this are not listed in the HUD or pinned to the chart's right edge", Order = 7, GroupName = "1. Filters")]
        public int LookforwardMinutes { get; set; }
        #endregion

        #region Properties - 2. Appearance
        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Theme", Description = "Auto follows the chart background. Custom lets you pick every color.", Order = 1, GroupName = "2. Appearance")]
        public icPANewsTheme Theme { get; set; }

        [Range(0.0, 1.0)]
        [Display(Name = "Background Opacity", Description = "HUD and label background opacity (0 to 1)", Order = 2, GroupName = "2. Appearance")]
        public double HudBackgroundOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Background Color", Description = "HUD panel background color (Custom theme)", Order = 3, GroupName = "2. Appearance")]
        public System.Windows.Media.Brush HudBackgroundBrush { get; set; }
        [Browsable(false)]
        public string HudBackgroundBrushSerialize
        {
            get { return Serialize.BrushToString(HudBackgroundBrush); }
            set { HudBackgroundBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Border Color", Description = "HUD panel border color (Custom theme)", Order = 4, GroupName = "2. Appearance")]
        public System.Windows.Media.Brush HudBorderBrush { get; set; }
        [Browsable(false)]
        public string HudBorderBrushSerialize
        {
            get { return Serialize.BrushToString(HudBorderBrush); }
            set { HudBorderBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Text Color", Description = "Main HUD text color (Custom theme)", Order = 5, GroupName = "2. Appearance")]
        public System.Windows.Media.Brush HudTextBrush { get; set; }
        [Browsable(false)]
        public string HudTextBrushSerialize
        {
            get { return Serialize.BrushToString(HudTextBrush); }
            set { HudTextBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Dimmed Text Color", Description = "Secondary text color: countdowns, low impact and holidays (Custom theme)", Order = 6, GroupName = "2. Appearance")]
        public System.Windows.Media.Brush HudDimTextBrush { get; set; }
        [Browsable(false)]
        public string HudDimTextBrushSerialize
        {
            get { return Serialize.BrushToString(HudDimTextBrush); }
            set { HudDimTextBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "High Impact Color", Description = "Color for high impact events (Custom theme)", Order = 7, GroupName = "2. Appearance")]
        public System.Windows.Media.Brush HighImpactColor { get; set; }
        [Browsable(false)]
        public string HighImpactColorSerialize
        {
            get { return Serialize.BrushToString(HighImpactColor); }
            set { HighImpactColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Medium Impact Color", Description = "Color for medium impact events (Custom theme)", Order = 8, GroupName = "2. Appearance")]
        public System.Windows.Media.Brush MedImpactColor { get; set; }
        [Browsable(false)]
        public string MedImpactColorSerialize
        {
            get { return Serialize.BrushToString(MedImpactColor); }
            set { MedImpactColor = Serialize.StringToBrush(value); }
        }
        #endregion

        #region Properties - 3. HUD Dashboard
        [Display(Name = "Show HUD Panel", Description = "Display table panel on chart", Order = 1, GroupName = "3. HUD Dashboard")]
        public bool ShowHUD { get; set; }

        [Display(Name = "HUD Corner", Description = "Corner of chart to dock the panel", Order = 2, GroupName = "3. HUD Dashboard")]
        public TextPosition CornerPosition { get; set; }

        [Range(10, 24)]
        [Display(Name = "Font Size", Description = "HUD text font size", Order = 3, GroupName = "3. HUD Dashboard")]
        public int HudFontSize { get; set; }

        [Range(1, 50)]
        [Display(Name = "Max HUD Rows", Description = "Maximum number of events listed in the HUD", Order = 4, GroupName = "3. HUD Dashboard")]
        public int MaxHudRows { get; set; }

        [Range(0, 2000)]
        [Display(Name = "HUD Offset X", Description = "Horizontal distance in pixels from the docked corner", Order = 5, GroupName = "3. HUD Dashboard")]
        public int HudOffsetX { get; set; }

        [Range(0, 2000)]
        [Display(Name = "HUD Offset Y", Description = "Vertical distance in pixels from the docked corner. The default keeps top corners clear of the chart's label line.", Order = 6, GroupName = "3. HUD Dashboard")]
        public int HudOffsetY { get; set; }

        [Display(Name = "Show Indicator Label", Description = "Show the indicator name in the chart's top-left label line", Order = 7, GroupName = "3. HUD Dashboard")]
        public bool ShowIndicatorLabel { get; set; }

        [Display(Name = "Time Format", Description = "24-hour (13:30) or 12-hour (1:30 PM)", Order = 8, GroupName = "3. HUD Dashboard")]
        public icPANewsTimeFormat TimeFormat { get; set; }

        [Display(Name = "Dim Past Events", Description = "Draw events that have already happened in the dimmed text color", Order = 9, GroupName = "3. HUD Dashboard")]
        public bool DimPastEvents { get; set; }

        // Toggled by clicking the HUD; saved with the chart so it survives reloads
        [Browsable(false)]
        public bool HudCollapsed { get; set; }
        #endregion

        // Hides the chart label unless the user wants it.
        // The name is still returned during SetDefaults so the Indicators dialog lists it normally.
        public override string DisplayName
        {
            get { return (State == State.SetDefaults || ShowIndicatorLabel) ? Name : string.Empty; }
        }

        #region Properties - 4. Timeline Markers
        [Display(Name = "Show Vertical Lines", Description = "Draw vertical timeline markers on chart (hidden on Daily and higher charts)", Order = 1, GroupName = "4. Timeline Markers")]
        public bool ShowVerticalLines { get; set; }

        [Display(Name = "Lines Show", Description = "Entire Week = lines and zones for every matching event this week. Same Window As HUD = only events listed in the HUD.", Order = 2, GroupName = "4. Timeline Markers")]
        public icPANewsLineRange LineRange { get; set; }

        [Range(1, 10)]
        [Display(Name = "Line Thickness", Description = "Thickness of vertical timeline markers", Order = 3, GroupName = "4. Timeline Markers")]
        public float LineThickness { get; set; }

        [Display(Name = "Line Dash Style", Description = "Dash pattern of vertical timeline markers", Order = 4, GroupName = "4. Timeline Markers")]
        public DashStyleHelper LineDashPattern { get; set; }
        #endregion

        #region Properties - 5. News Window
        [Display(Name = "Shade News Zones", Description = "Shade the chart from 'Minutes Before' to 'Minutes After' each event", Order = 1, GroupName = "5. News Window")]
        public bool ShowNewsZones { get; set; }

        [Range(0, 240)]
        [Display(Name = "Minutes Before", Description = "News window start, in minutes before the event. Used by the zones and by IsInNewsWindow.", Order = 2, GroupName = "5. News Window")]
        public int NewsWindowMinutesBefore { get; set; }

        [Range(0, 240)]
        [Display(Name = "Minutes After", Description = "News window end, in minutes after the event. Used by the zones and by IsInNewsWindow.", Order = 3, GroupName = "5. News Window")]
        public int NewsWindowMinutesAfter { get; set; }

        [Range(0.0, 1.0)]
        [Display(Name = "Zone Opacity", Description = "Opacity of the shaded news zones (0 to 1)", Order = 4, GroupName = "5. News Window")]
        public double ZoneOpacity { get; set; }
        #endregion

        #region Properties - 6. Alerts
        [Display(Name = "Enable Alerts", Description = "Raise a NinjaTrader alert before high-impact events", Order = 1, GroupName = "6. Alerts")]
        public bool EnableAlerts { get; set; }

        [Range(1, 120)]
        [Display(Name = "Alert Minutes Before", Description = "How many minutes before the event to alert", Order = 2, GroupName = "6. Alerts")]
        public int AlertMinutesBefore { get; set; }

        [Display(Name = "Alert On Medium Impact", Description = "Also alert before medium impact events", Order = 3, GroupName = "6. Alerts")]
        public bool AlertOnMediumImpact { get; set; }

        [Display(Name = "Alert Sound", Description = "Sound file name from the NinjaTrader 'sounds' folder (e.g. Alert2.wav), a full path, or empty for no sound", Order = 4, GroupName = "6. Alerts")]
        public string AlertSound { get; set; }
        #endregion

        #region Public API for Strategies
        // Real time only: the feed covers the current week and has no history, so outside
        // State.Realtime these return false / NaN / empty. This also stops a backtest from
        // seeing today's live news window when charts have already loaded the calendar.

        /// <summary>True if now is within [event - MinutesBefore, event + MinutesAfter] of any event passing the filters.</summary>
        [Browsable(false)]
        [XmlIgnore]
        public bool IsInNewsWindow
        {
            get
            {
                if (State != State.Realtime)
                    return false;

                EnsureDataWhenHosted();
                DateTime nowUtc = DateTime.UtcNow;
                lock (sharedLock)
                {
                    foreach (var evt in sharedCachedEvents)
                    {
                        if (evt.IsAllDay || !PassesFilters(evt)) continue;
                        if (nowUtc >= evt.UtcDateTime.AddMinutes(-NewsWindowMinutesBefore) &&
                            nowUtc <= evt.UtcDateTime.AddMinutes(NewsWindowMinutesAfter))
                            return true;
                    }
                }
                return false;
            }
        }

        /// <summary>Minutes until the next event passing the filters, or double.NaN if none this week.</summary>
        [Browsable(false)]
        [XmlIgnore]
        public double MinutesToNextEvent
        {
            get
            {
                var next = FindNextEvent();
                return next == null ? double.NaN : (next.UtcDateTime - DateTime.UtcNow).TotalMinutes;
            }
        }

        /// <summary>e.g. "USD Non-Farm Employment Change (High)", or empty if none this week.</summary>
        [Browsable(false)]
        [XmlIgnore]
        public string NextEventDescription
        {
            get
            {
                var next = FindNextEvent();
                return next == null ? string.Empty : string.Format("{0} {1} ({2})", next.Country, next.Title, next.Impact);
            }
        }

        private icPANewsEventItem FindNextEvent()
        {
            if (State != State.Realtime)
                return null;

            EnsureDataWhenHosted();
            DateTime nowUtc = DateTime.UtcNow;
            icPANewsEventItem best = null;
            lock (sharedLock)
            {
                foreach (var evt in sharedCachedEvents)
                {
                    if (evt.IsAllDay || !PassesFilters(evt) || evt.UtcDateTime < nowUtc) continue;
                    if (best == null || evt.UtcDateTime < best.UtcDateTime)
                        best = evt;
                }
            }
            return best;
        }

        // When hosted by a strategy there is no chart timer, so refresh on demand (at most every 30 s)
        private void EnsureDataWhenHosted()
        {
            if (ChartControl != null)
                return;

            DateTime nowUtc = DateTime.UtcNow;
            if ((nowUtc - lastHostRefreshUtc).TotalSeconds < 30)
                return;
            lastHostRefreshUtc = nowUtc;

            bool empty;
            lock (sharedLock)
                empty = sharedCachedEvents.Count == 0;

            if (empty || (IsCacheStale() && !IsInBackoff()))
                Task.Run(() => LoadCalendarDataAsync());
        }
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Economic calendar on the chart from the Forex Factory weekly feed: a panel with countdowns, vertical event markers, shaded news windows and optional alerts. Works on any chart (Daily and higher show the panel only) and needs an internet connection. v" + IndicatorVersion + "\r\n"
                            + "Developed by Istvan Cebrian | https://icpriceaction.com\r"
                            + "Support this free tool: https://buymeacoffee.com/icpriceaction";
                Name = "icPA - News";
                Calculate = Calculate.OnBarClose;   // OnBarUpdate is empty; rendering is driven by the timer / ForceRefresh
                IsOverlay = true;
                DisplayInDataBox = false;

                // Filters
                ShowHighImpact = true;
                ShowMediumImpact = false;
                ShowLowImpact = false;
                ShowHolidays = false;
                Currencies = "Auto";
                LookbackMinutes = 120;
                LookforwardMinutes = 240;

                // Appearance (custom colors default to the Dark palette)
                Theme = icPANewsTheme.Auto;
                HudBackgroundOpacity = 0.90;
                HudBackgroundBrush = CreateFrozenBrush(DarkPalette.Background);
                HudBorderBrush = CreateFrozenBrush(DarkPalette.Border);
                HudTextBrush = CreateFrozenBrush(DarkPalette.Text);
                HudDimTextBrush = CreateFrozenBrush(DarkPalette.DimText);
                HighImpactColor = CreateFrozenBrush(DarkPalette.HighImpact);
                MedImpactColor = CreateFrozenBrush(DarkPalette.MedImpact);

                // HUD
                ShowHUD = true;
                CornerPosition = TextPosition.TopLeft;
                HudFontSize = 16;
                MaxHudRows = 8;
                HudOffsetX = 22;
                HudOffsetY = 22;
                ShowIndicatorLabel = false;
                TimeFormat = icPANewsTimeFormat.TwentyFourHour;
                DimPastEvents = true;
                HudCollapsed = false;

                // Timeline markers
                ShowVerticalLines = true;
                LineRange = icPANewsLineRange.EntireWeek;
                LineThickness = 1.5f;
                LineDashPattern = DashStyleHelper.Dash;

                // News window
                ShowNewsZones = true;
                NewsWindowMinutesBefore = 5;
                NewsWindowMinutesAfter = 5;
                ZoneOpacity = 0.12;

                // Alerts
                EnableAlerts = false;
                AlertMinutesBefore = 5;
                AlertOnMediumImpact = false;
                AlertSound = "Alert2.wav";
            }
            else if (State == State.DataLoaded)
            {
                DetectCurrencies();
                ResolveChartTimeMode();

                headerTextExpanded = string.Format("\u25BE ECONOMIC CALENDAR ({0})", currencyHeader);
                headerTextCollapsed = string.Format("\u25B8 ECONOMIC CALENDAR ({0})", currencyHeader);
            }
            else if (State == State.Historical)
            {
                // Render above drawing objects and other indicators in this panel
                SetZOrder(int.MaxValue);

                // Timer and mouse handling are chart-only. Strategies and analyzers have no
                // ChartControl; a live strategy loads the calendar in State.Realtime instead.
                if (ChartControl == null || ChartControl.Dispatcher == null)
                    return;

                // Uses the memory or disk cache first; downloads only if the cache is stale
                Task.Run(() => LoadCalendarDataAsync());

                ChartControl.Dispatcher.InvokeAsync(() =>
                {
                    // The indicator may have been removed before this ran; don't create an orphan timer
                    if (State == State.Terminated || updateTimer != null)
                        return;

                    updateTimer = new System.Windows.Threading.DispatcherTimer();
                    updateTimer.Interval = TimeSpan.FromSeconds(10);
                    updateTimer.Tick += OnTimerTick;
                    updateTimer.Start();

                    // Click-to-collapse. Listen on the ChartControl and also receive clicks that were
                    // already marked handled: when the panel sits over price bars, the chart's own
                    // selection handling can consume the click before it reaches the chart panel.
                    if (mouseSubscribedControl == null && ChartControl != null)
                    {
                        mouseDownHandler = new System.Windows.Input.MouseButtonEventHandler(OnChartMouseDown);
                        ChartControl.AddHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, mouseDownHandler, true);
                        mouseSubscribedControl = ChartControl;
                    }
                });
            }
            else if (State == State.Realtime)
            {
                // Hosted by a live strategy (no chart): load the calendar now. Backtests and
                // optimizations never reach Realtime, so they never trigger downloads.
                if (ChartControl == null)
                    Task.Run(() => LoadCalendarDataAsync());
            }
            else if (State == State.Terminated)
            {
                // Terminated can run on instances that never got past SetDefaults: null-check everything
                if (updateTimer != null)
                {
                    var timer = updateTimer;
                    updateTimer = null;
                    timer.Tick -= OnTimerTick;

                    // DispatcherTimer belongs to the UI thread; stop it there
                    if (timer.Dispatcher != null && !timer.Dispatcher.CheckAccess())
                        timer.Dispatcher.InvokeAsync(() => timer.Stop());
                    else
                        timer.Stop();
                }

                if (mouseSubscribedControl != null && mouseDownHandler != null)
                {
                    var control = mouseSubscribedControl;
                    var handler = mouseDownHandler;
                    mouseSubscribedControl = null;
                    mouseDownHandler = null;
                    if (control.Dispatcher != null)
                        control.Dispatcher.InvokeAsync(() => control.RemoveHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, handler));
                }

                DisposeSharpDxResources();
            }
        }

        protected override void OnBarUpdate()
        {
        }

        private static System.Windows.Media.Brush CreateFrozenBrush(System.Windows.Media.Color color)
        {
            var brush = new System.Windows.Media.SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        #region Chart Type Detection
        private void ResolveChartTimeMode()
        {
            timeBasedChart = true;
            dailyOrHigherChart = false;

            if (BarsPeriod == null)
                return;

            BarsPeriodType type = BarsPeriod.BarsPeriodType;

            // Heiken Ashi is built on a base period; judge by that
            if (type == BarsPeriodType.HeikenAshi)
                type = BarsPeriod.BaseBarsPeriodType;

            switch (type)
            {
                case BarsPeriodType.Second:
                case BarsPeriodType.Minute:
                    timeBasedChart = true;
                    dailyOrHigherChart = false;
                    break;

                case BarsPeriodType.Day:
                case BarsPeriodType.Week:
                case BarsPeriodType.Month:
                case BarsPeriodType.Year:
                    timeBasedChart = true;
                    dailyOrHigherChart = true;
                    break;

                default:
                    // Tick, Volume, Range, Renko, Kagi, LineBreak, PointAndFigure and third-party bar types
                    timeBasedChart = false;
                    dailyOrHigherChart = false;
                    break;
            }
        }
        #endregion

        #region Symbol & Currency Detection
        private void DetectCurrencies()
        {
            targetCurrencies.Clear();
            string setting = (Currencies ?? "Auto").Trim();

            if (setting.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                currencyHeader = "ALL";
                return;
            }

            if (setting.Length > 0 && !setting.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string token in setting.Split(new[] { ',', ';', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string code = token.Trim().ToUpperInvariant();
                    if (code.Length == 3)
                        targetCurrencies.Add(code);
                }

                if (targetCurrencies.Count > 0)
                {
                    currencyHeader = string.Join("/", targetCurrencies);
                    return;
                }

                Print(string.Format("[icPANews] Could not read Currencies setting '{0}'; using Auto.", setting));
            }

            AutoDetectCurrencies();
            currencyHeader = targetCurrencies.Count > 0 ? string.Join("/", targetCurrencies) : "ALL";
        }

        private void AutoDetectCurrencies()
        {
            MasterInstrument mi = Instrument != null ? Instrument.MasterInstrument : null;
            if (mi == null)
            {
                targetCurrencies.Add("USD");
                return;
            }

            string sym = (mi.Name ?? string.Empty).ToUpperInvariant();

            // 1. FX pairs: spot forex, or a 6-letter CFD made of two known currencies
            if (sym.Length == 6)
            {
                string a = sym.Substring(0, 3);
                string b = sym.Substring(3, 3);
                bool isForex = mi.InstrumentType == InstrumentType.Forex;

                if (isForex || (FeedCurrencies.Contains(a) && FeedCurrencies.Contains(b)))
                {
                    if (FeedCurrencies.Contains(a)) targetCurrencies.Add(a);
                    if (FeedCurrencies.Contains(b)) targetCurrencies.Add(b);
                    if (targetCurrencies.Count > 0)
                        return;
                }
            }

            // 2. Known futures contracts
            string[] mapped;
            if (FuturesCurrencyMap.TryGetValue(sym, out mapped))
            {
                foreach (string c in mapped)
                    targetCurrencies.Add(c);
                return;
            }

            // 3. Name hints (CFDs and broker-specific index names)
            if (sym.Contains("DAX") || sym.Contains("STOXX") || sym.Contains("ESTX") || sym.Contains("GER"))
            {
                targetCurrencies.Add("EUR");
                return;
            }
            if (sym.Contains("FTSE") || sym.Contains("UK100"))
            {
                targetCurrencies.Add("GBP");
                return;
            }
            if (sym.Contains("NIKKEI") || sym.Contains("JP225"))
            {
                targetCurrencies.Add("JPY");
                return;
            }

            // 4. The instrument's own trading currency
            string own = CurrencyToCode(mi.Currency);
            if (own != null)
            {
                targetCurrencies.Add(own);
                return;
            }

            // 5. Last resort
            targetCurrencies.Add("USD");
        }

        // Maps NinjaTrader's currency enum to a Forex Factory code. Takes object and compares by name
        // so it compiles regardless of which enum members exist in a given NinjaTrader version.
        private static string CurrencyToCode(object currency)
        {
            if (currency == null) return null;
            string name = currency.ToString();

            if (FeedCurrencies.Contains(name))
                return name.ToUpperInvariant();

            switch (name)
            {
                case "UsDollar":         return "USD";
                case "Euro":             return "EUR";
                case "BritishPound":     return "GBP";
                case "JapaneseYen":      return "JPY";
                case "AustralianDollar": return "AUD";
                case "NewZealandDollar": return "NZD";
                case "CanadianDollar":   return "CAD";
                case "SwissFranc":       return "CHF";
                case "ChineseYuan":      return "CNY";
                default:                 return null;
            }
        }
        #endregion

        #region Disk & Network Feed Management
        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; icPANews-NinjaTrader/" + IndicatorVersion + ")");
            return client;
        }

        private static string GetDiskCacheFilePath()
        {
            try
            {
                string dir = NinjaTrader.Core.Globals.UserDataDir;
                return Path.Combine(dir, CacheFileName);
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), CacheFileName);
            }
        }

        private static bool IsCacheStale()
        {
            lock (sharedLock)
                return (DateTime.UtcNow - sharedLastDownloadUtc).TotalMinutes >= CacheMinutes;
        }

        private static bool IsInBackoff()
        {
            lock (sharedLock)
                return DateTime.UtcNow < sharedNextAttemptUtc;
        }

        private async Task LoadCalendarDataAsync()
        {
            string cachePath = GetDiskCacheFilePath();

            // 1. Check in-memory shared cache
            bool haveMemoryCache;
            lock (sharedLock)
                haveMemoryCache = sharedCachedEvents.Count > 0;

            if (haveMemoryCache && (!IsCacheStale() || IsInBackoff()))
            {
                FilterActiveEvents();
                return;
            }

            // 2. Check disk cache if in-memory cache is empty or stale
            if (File.Exists(cachePath))
            {
                try
                {
                    var fileInfo = new FileInfo(cachePath);
                    DateTime lastDownload;
                    lock (sharedLock)
                        lastDownload = sharedLastDownloadUtc;

                    if (fileInfo.Length > 0 && fileInfo.LastWriteTimeUtc > lastDownload)
                    {
                        string localJson = File.ReadAllText(cachePath);
                        var diskEvents = ParseForexFactoryJson(localJson);

                        if (diskEvents.Count > 0)
                        {
                            lock (sharedLock)
                            {
                                sharedCachedEvents.Clear();
                                sharedCachedEvents.AddRange(diskEvents);
                                sharedLastDownloadUtc = fileInfo.LastWriteTimeUtc;
                            }
                            FilterActiveEvents();

                            if (!IsCacheStale())
                                return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Print(string.Format("[icPANews] Error reading local disk cache: {0}", ex.Message));
                }
            }

            // 3. Cache is stale or missing: fetch online
            await FetchOnlineFeedWithLockAsync(cachePath).ConfigureAwait(false);
        }

        private async Task FetchOnlineFeedWithLockAsync(string cachePath)
        {
            // Ensure only one download runs across all charts.
            // If another chart is downloading, show whatever we have now; we'll pick up fresh data on the next tick.
            if (!await downloadSemaphore.WaitAsync(0).ConfigureAwait(false))
            {
                FilterActiveEvents();
                return;
            }

            try
            {
                // Re-check freshness / backoff once inside the lock
                if (!IsCacheStale() || IsInBackoff())
                {
                    FilterActiveEvents();
                    return;
                }

                using (var response = await httpClient.GetAsync(FeedUrl).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 429)
                    {
                        bool firstNotice;
                        lock (sharedLock)
                        {
                            sharedNextAttemptUtc = DateTime.UtcNow.AddMinutes(RateLimitBackoffMinutes);
                            firstNotice = !isRateLimited;
                            isRateLimited = true;
                        }

                        if (firstNotice)
                            Print("[icPANews] Forex Factory rate limit (429) active. Using cached calendar data; will retry later.");

                        FilterActiveEvents();
                        return;
                    }

                    response.EnsureSuccessStatusCode();
                    lock (sharedLock)
                        isRateLimited = false;

                    string jsonContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var parsedList = ParseForexFactoryJson(jsonContent);

                    if (parsedList.Count > 0)
                    {
                        lock (sharedLock)
                        {
                            sharedCachedEvents.Clear();
                            sharedCachedEvents.AddRange(parsedList);
                            sharedLastDownloadUtc = DateTime.UtcNow;
                            sharedNextAttemptUtc = DateTime.MinValue;
                        }

                        WriteDiskCache(cachePath, jsonContent);
                    }
                    else
                    {
                        // Unexpected payload: don't retry every 10 seconds
                        lock (sharedLock)
                            sharedNextAttemptUtc = DateTime.UtcNow.AddMinutes(ErrorBackoffMinutes);
                        Print("[icPANews] Feed downloaded but no events could be parsed; keeping cached data.");
                    }

                    FilterActiveEvents();
                }
            }
            catch (Exception ex)
            {
                // Network error / timeout: back off instead of retrying on every timer tick
                lock (sharedLock)
                    sharedNextAttemptUtc = DateTime.UtcNow.AddMinutes(ErrorBackoffMinutes);
                Print(string.Format("[icPANews] Feed download notice: {0}", ex.Message));
                FilterActiveEvents();
            }
            finally
            {
                downloadSemaphore.Release();
            }
        }

        // Writes to a temporary file first, then swaps it in, so a chart reading the cache
        // at the same moment never sees a half-written file
        private static void WriteDiskCache(string cachePath, string json)
        {
            string tempPath = cachePath + ".tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                if (File.Exists(cachePath))
                    File.Replace(tempPath, cachePath, null);
                else
                    File.Move(tempPath, cachePath);
            }
            catch
            {
                // The cache is only an optimisation; the data is already in memory
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { }
            }
        }

        private static List<icPANewsEventItem> ParseForexFactoryJson(string json)
        {
            var results = new List<icPANewsEventItem>();
            if (string.IsNullOrWhiteSpace(json)) return results;

            List<object> root;
            try
            {
                root = MiniJson.Parse(json) as List<object>;
            }
            catch
            {
                return results;   // not JSON (e.g. an HTML error page); caller handles the empty result
            }

            if (root == null) return results;

            foreach (object item in root)
            {
                var obj = item as Dictionary<string, object>;
                if (obj == null) continue;

                DateTimeOffset dto;
                if (!DateTimeOffset.TryParse(GetJsonString(obj, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
                    continue;

                string title = GetJsonString(obj, "title");
                string country = GetJsonString(obj, "country");
                if (title.Length == 0 || country.Length == 0) continue;

                results.Add(new icPANewsEventItem
                {
                    Title = title,
                    Country = country,
                    UtcDateTime = dto.UtcDateTime,
                    FeedDate = dto.Date,
                    // All-day and tentative events are published at midnight in the feed's own time zone
                    IsAllDay = dto.TimeOfDay == TimeSpan.Zero,
                    Impact = GetJsonString(obj, "impact"),
                    Forecast = GetJsonString(obj, "forecast"),
                    Previous = GetJsonString(obj, "previous")
                });
            }

            return results;
        }

        private static string GetJsonString(Dictionary<string, object> obj, string key)
        {
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) return string.Empty;
            if (value is double) return ((double)value).ToString(CultureInfo.InvariantCulture);
            return value.ToString().Trim();
        }

        // 3 = High, 2 = Medium, 1 = Low, 0 = Holiday / other
        private static int ImpactRank(icPANewsEventItem evt)
        {
            string impact = evt.Impact ?? string.Empty;
            if (impact.Equals("High", StringComparison.OrdinalIgnoreCase)) return 3;
            if (impact.Equals("Medium", StringComparison.OrdinalIgnoreCase)) return 2;
            if (impact.Equals("Low", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        private static bool IsHoliday(icPANewsEventItem evt)
        {
            return evt.Impact != null && evt.Impact.Equals("Holiday", StringComparison.OrdinalIgnoreCase);
        }

        // Impact and currency filters (shared by the HUD, lines, zones, alerts and the strategy API)
        private bool PassesFilters(icPANewsEventItem evt)
        {
            bool impactOk;
            switch (ImpactRank(evt))
            {
                case 3:  impactOk = ShowHighImpact; break;
                case 2:  impactOk = ShowMediumImpact; break;
                case 1:  impactOk = ShowLowImpact; break;
                default: impactOk = ShowHolidays && IsHoliday(evt); break;
            }

            if (!impactOk)
                return false;

            if (targetCurrencies.Count > 0 && !targetCurrencies.Contains(evt.Country))
                return false;

            return true;
        }

        private void FilterActiveEvents()
        {
            if (State == State.Terminated)
                return;

            DateTime nowUtc = DateTime.UtcNow;
            DateTime minUtc = nowUtc.AddMinutes(-LookbackMinutes);
            DateTime maxUtc = nowUtc.AddMinutes(LookforwardMinutes);
            bool linesForWholeWeek = LineRange == icPANewsLineRange.EntireWeek;

            lock (sharedLock)
            {
                lock (instanceLock)
                {
                    hudEvents.Clear();
                    lineEvents.Clear();

                    foreach (var evt in sharedCachedEvents)
                    {
                        if (!PassesFilters(evt))
                            continue;

                        // All-day events count as active for their whole day
                        DateTime start = evt.UtcDateTime;
                        DateTime end = evt.IsAllDay ? start.AddDays(1) : start;
                        bool inHudWindow = end >= minUtc && start <= maxUtc;

                        if (inHudWindow)
                            hudEvents.Add(evt);

                        if (linesForWholeWeek || inHudWindow)
                            lineEvents.Add(evt);
                    }

                    hudEvents.Sort(CompareByTime);
                    lineEvents.Sort(CompareByTime);
                }
            }

            var cc = ChartControl;
            if (cc != null && cc.Dispatcher != null)
            {
                cc.Dispatcher.InvokeAsync(() =>
                {
                    if (State != State.Terminated)
                        ForceRefresh();
                });
            }
        }

        private static int CompareByTime(icPANewsEventItem a, icPANewsEventItem b)
        {
            return a.UtcDateTime.CompareTo(b.UtcDateTime);
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (State == State.Terminated)
                return;

            // Only attempt an update if the cache is stale and we're not backing off after an error / 429
            if (IsCacheStale() && !IsInBackoff())
                Task.Run(() => LoadCalendarDataAsync());
            else
                FilterActiveEvents();

            CheckAlerts();
        }
        #endregion

        #region Alerts
        private void CheckAlerts()
        {
            // Only in real time: before that, NinjaTrader ignores alerts and we'd lose them
            if (!EnableAlerts || State != State.Realtime)
                return;

            DateTime nowUtc = DateTime.UtcNow;
            List<KeyValuePair<string, icPANewsEventItem>> due = null;

            lock (sharedLock)
            {
                foreach (var evt in sharedCachedEvents)
                {
                    if (evt.IsAllDay)
                        continue;

                    int rank = ImpactRank(evt);
                    if (rank < 3 && !(rank == 2 && AlertOnMediumImpact))
                        continue;

                    if (!PassesFilters(evt))
                        continue;

                    double minutesTo = (evt.UtcDateTime - nowUtc).TotalMinutes;
                    if (minutesTo < 0 || minutesTo > AlertMinutesBefore)
                        continue;

                    string key = string.Format("{0}|{1}|{2}|{3}", AlertMinutesBefore, evt.Country, evt.Title, evt.UtcDateTime.Ticks);
                    if (sharedAlertedKeys.Add(key))
                    {
                        if (due == null)
                            due = new List<KeyValuePair<string, icPANewsEventItem>>();
                        due.Add(new KeyValuePair<string, icPANewsEventItem>(key, evt));
                    }
                }
            }

            if (due == null)
                return;

            foreach (var item in due)
                FireAlert(item.Key, item.Value, nowUtc);
        }

        private void FireAlert(string key, icPANewsEventItem evt, DateTime nowUtc)
        {
            bool high = ImpactRank(evt) == 3;
            int minutes = Math.Max(1, (int)Math.Ceiling((evt.UtcDateTime - nowUtc).TotalMinutes));
            string localTime = FormatClockTime(TimeZoneInfo.ConvertTimeFromUtc(evt.UtcDateTime, GetConfiguredTimeZone()));
            string message = string.Format("{0} {1} at {2} (in {3} min)", evt.Country, evt.Title, localTime, minutes);
            string soundPath = ResolveAlertSoundPath();
            string alertId = "icPANews_" + key.GetHashCode().ToString(CultureInfo.InvariantCulture);

            System.Windows.Media.Brush backBrush = high ? System.Windows.Media.Brushes.DarkRed : System.Windows.Media.Brushes.DarkOrange;
            System.Windows.Media.Brush foreBrush = System.Windows.Media.Brushes.White;

            try
            {
                // Alerts must be raised on the instrument's thread; TriggerCustomEvent handles that from the timer
                TriggerCustomEvent(o =>
                {
                    Alert(alertId, high ? Priority.High : Priority.Medium, message, soundPath, 300, backBrush, foreBrush);
                }, null);
            }
            catch (Exception ex)
            {
                Print(string.Format("[icPANews] Could not raise alert: {0}", ex.Message));
            }
        }

        private string ResolveAlertSoundPath()
        {
            string sound = (AlertSound ?? string.Empty).Trim();
            if (sound.Length == 0)
                return string.Empty;

            try
            {
                if (Path.IsPathRooted(sound))
                    return sound;
                return Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds", sound);
            }
            catch
            {
                return string.Empty;
            }
        }
        #endregion

        #region Minimal JSON Parser
        // Small, tolerant JSON reader so the indicator needs no external DLL references.
        // Objects -> Dictionary<string, object>, arrays -> List<object>, numbers -> double.
        private static class MiniJson
        {
            public static object Parse(string s)
            {
                int i = 0;
                object value = ParseValue(s, ref i);
                return value;
            }

            private static void SkipWhitespace(string s, ref int i)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            private static char Peek(string s, int i)
            {
                if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
                return s[i];
            }

            private static object ParseValue(string s, ref int i)
            {
                SkipWhitespace(s, ref i);
                char c = Peek(s, i);
                switch (c)
                {
                    case '{': return ParseObject(s, ref i);
                    case '[': return ParseArray(s, ref i);
                    case '"': return ParseString(s, ref i);
                    case 't': Expect(s, ref i, "true"); return true;
                    case 'f': Expect(s, ref i, "false"); return false;
                    case 'n': Expect(s, ref i, "null"); return null;
                    default: return ParseNumber(s, ref i);
                }
            }

            private static Dictionary<string, object> ParseObject(string s, ref int i)
            {
                var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                i++; // {
                SkipWhitespace(s, ref i);
                if (Peek(s, i) == '}') { i++; return result; }

                while (true)
                {
                    SkipWhitespace(s, ref i);
                    if (Peek(s, i) != '"') throw new FormatException("Expected property name");
                    string key = ParseString(s, ref i);

                    SkipWhitespace(s, ref i);
                    if (Peek(s, i) != ':') throw new FormatException("Expected ':'");
                    i++;

                    result[key] = ParseValue(s, ref i);

                    SkipWhitespace(s, ref i);
                    char c = Peek(s, i);
                    i++;
                    if (c == ',') continue;
                    if (c == '}') return result;
                    throw new FormatException("Expected ',' or '}'");
                }
            }

            private static List<object> ParseArray(string s, ref int i)
            {
                var result = new List<object>();
                i++; // [
                SkipWhitespace(s, ref i);
                if (Peek(s, i) == ']') { i++; return result; }

                while (true)
                {
                    result.Add(ParseValue(s, ref i));

                    SkipWhitespace(s, ref i);
                    char c = Peek(s, i);
                    i++;
                    if (c == ',') continue;
                    if (c == ']') return result;
                    throw new FormatException("Expected ',' or ']'");
                }
            }

            private static string ParseString(string s, ref int i)
            {
                var sb = new StringBuilder();
                i++; // opening quote

                while (true)
                {
                    char c = Peek(s, i);
                    i++;

                    if (c == '"')
                        return sb.ToString();

                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    char e = Peek(s, i);
                    i++;
                    switch (e)
                    {
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("Bad unicode escape");
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                            break;
                        default: sb.Append(e); break;   // \" \\ \/ and anything unexpected
                    }
                }
            }

            private static double ParseNumber(string s, ref int i)
            {
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (i == start) throw new FormatException("Unexpected character in JSON");
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private static void Expect(string s, ref int i, string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                    throw new FormatException("Unexpected token in JSON");
                i += word.Length;
            }
        }
        #endregion

        #region Theme Resolution
        // Returns Dark, Light or Custom. Auto is resolved from the chart background brightness.
        private icPANewsTheme ResolveTheme(ChartControl chartControl)
        {
            if (Theme != icPANewsTheme.Auto)
                return Theme;

            try
            {
                var bg = chartControl != null && chartControl.Properties != null
                    ? chartControl.Properties.ChartBackground as System.Windows.Media.SolidColorBrush
                    : null;

                if (bg != null)
                {
                    var c = bg.Color;
                    double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                    return luminance > 0.5 ? icPANewsTheme.Light : icPANewsTheme.Dark;
                }
            }
            catch { }

            return icPANewsTheme.Dark;
        }

        private static System.Windows.Media.Color BrushColor(System.Windows.Media.Brush brush, System.Windows.Media.Color fallback)
        {
            var solid = brush as System.Windows.Media.SolidColorBrush;
            return solid != null ? solid.Color : fallback;   // non-solid (e.g. gradient) brushes fall back
        }

        private ThemeColors GetThemeColors(icPANewsTheme theme)
        {
            switch (theme)
            {
                case icPANewsTheme.Light:
                    return LightPalette;

                case icPANewsTheme.Custom:
                    return new ThemeColors
                    {
                        Background = BrushColor(HudBackgroundBrush, DarkPalette.Background),
                        Border     = BrushColor(HudBorderBrush, DarkPalette.Border),
                        Text       = BrushColor(HudTextBrush, DarkPalette.Text),
                        DimText    = BrushColor(HudDimTextBrush, DarkPalette.DimText),
                        HighImpact = BrushColor(HighImpactColor, DarkPalette.HighImpact),
                        MedImpact  = BrushColor(MedImpactColor, DarkPalette.MedImpact)
                    };

                default:
                    return DarkPalette;
            }
        }
        #endregion

        #region SharpDX Direct2D Rendering
        private TimeZoneInfo GetConfiguredTimeZone()
        {
            try
            {
                if (NinjaTrader.Core.Globals.GeneralOptions != null && NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo != null)
                    return NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
            }
            catch { }

            return TimeZoneInfo.Local;
        }

        // Device resources are (re)built here whenever the chart gives us a new render target
        public override void OnRenderTargetChanged()
        {
            DisposeSharpDxResources();
            base.OnRenderTargetChanged();

            if (RenderTarget != null)
            {
                resolvedTheme = ResolveTheme(ChartControl);
                CreateSharpDxResources(RenderTarget);
            }
        }

        private void DisposeSharpDxResources()
        {
            if (sharpDxHudBgBrush != null) { sharpDxHudBgBrush.Dispose(); sharpDxHudBgBrush = null; }
            if (sharpDxHudBorderBrush != null) { sharpDxHudBorderBrush.Dispose(); sharpDxHudBorderBrush = null; }
            if (sharpDxHighImpactBrush != null) { sharpDxHighImpactBrush.Dispose(); sharpDxHighImpactBrush = null; }
            if (sharpDxMedImpactBrush != null) { sharpDxMedImpactBrush.Dispose(); sharpDxMedImpactBrush = null; }
            if (sharpDxTextBrush != null) { sharpDxTextBrush.Dispose(); sharpDxTextBrush = null; }
            if (sharpDxDimTextBrush != null) { sharpDxDimTextBrush.Dispose(); sharpDxDimTextBrush = null; }
            if (sharpDxHighZoneBrush != null) { sharpDxHighZoneBrush.Dispose(); sharpDxHighZoneBrush = null; }
            if (sharpDxMedZoneBrush != null) { sharpDxMedZoneBrush.Dispose(); sharpDxMedZoneBrush = null; }
            if (sharpDxLowZoneBrush != null) { sharpDxLowZoneBrush.Dispose(); sharpDxLowZoneBrush = null; }
            if (sharpDxStrokeStyle != null) { sharpDxStrokeStyle.Dispose(); sharpDxStrokeStyle = null; }
            if (sharpDxTextFormat != null) { sharpDxTextFormat.Dispose(); sharpDxTextFormat = null; }
            if (sharpDxBoldFormat != null) { sharpDxBoldFormat.Dispose(); sharpDxBoldFormat = null; }
            if (sharpDxSmallFormat != null) { sharpDxSmallFormat.Dispose(); sharpDxSmallFormat = null; }

            textWidthCache.Clear();
            textHeightCache.Clear();
        }

        private static SharpDX.Color4 ToColor4(System.Windows.Media.Color c, float alpha)
        {
            return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
        }

        private SharpDX.DirectWrite.TextFormat CreateTextFormat(SharpDX.DirectWrite.FontWeight weight, float size)
        {
            var format = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", weight, SharpDX.DirectWrite.FontStyle.Normal, size);
            format.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
            format.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
            return format;
        }

        private void CreateSharpDxResources(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (rt == null || sharpDxTextFormat != null) return;

            sharpDxTextFormat = CreateTextFormat(SharpDX.DirectWrite.FontWeight.Normal, HudFontSize);
            sharpDxBoldFormat = CreateTextFormat(SharpDX.DirectWrite.FontWeight.Bold, HudFontSize);
            sharpDxSmallFormat = CreateTextFormat(SharpDX.DirectWrite.FontWeight.Normal, Math.Max(9, HudFontSize - 2));

            ThemeColors colors = GetThemeColors(resolvedTheme);
            float zoneAlpha = (float)ZoneOpacity;

            sharpDxHudBgBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(colors.Background, (float)HudBackgroundOpacity));
            sharpDxHudBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(colors.Border, 1f));
            sharpDxTextBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(colors.Text, 1f));
            sharpDxDimTextBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(colors.DimText, 1f));
            sharpDxHighImpactBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(colors.HighImpact, 1f));
            sharpDxMedImpactBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(colors.MedImpact, 1f));
            sharpDxHighZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(colors.HighImpact, zoneAlpha));
            sharpDxMedZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(colors.MedImpact, zoneAlpha));
            sharpDxLowZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(colors.DimText, zoneAlpha));

            // Line Style
            SharpDX.Direct2D1.DashStyle ds = SharpDX.Direct2D1.DashStyle.Solid;
            switch (LineDashPattern)
            {
                case DashStyleHelper.Dash:       ds = SharpDX.Direct2D1.DashStyle.Dash; break;
                case DashStyleHelper.DashDot:    ds = SharpDX.Direct2D1.DashStyle.DashDot; break;
                case DashStyleHelper.DashDotDot: ds = SharpDX.Direct2D1.DashStyle.DashDotDot; break;
                case DashStyleHelper.Dot:        ds = SharpDX.Direct2D1.DashStyle.Dot; break;
            }

            sharpDxStrokeStyle = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = ds });
        }

        // Solid impact color for text and lines
        private SharpDX.Direct2D1.SolidColorBrush GetImpactBrush(int rank)
        {
            if (rank >= 3) return sharpDxHighImpactBrush;
            if (rank == 2) return sharpDxMedImpactBrush;
            return sharpDxDimTextBrush;
        }

        // Translucent impact color for news zones
        private SharpDX.Direct2D1.SolidColorBrush GetZoneBrush(int rank)
        {
            if (rank >= 3) return sharpDxHighZoneBrush;
            if (rank == 2) return sharpDxMedZoneBrush;
            return sharpDxLowZoneBrush;
        }

        #region Text Measurement Helpers
        // TextLayout objects are only created for strings not yet in the cache, always inside a using block
        private float MeasureWidth(string text, SharpDX.DirectWrite.TextFormat format)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            Dictionary<string, float> cache;
            if (!textWidthCache.TryGetValue(format, out cache))
            {
                cache = new Dictionary<string, float>();
                textWidthCache[format] = cache;
            }

            float width;
            if (cache.TryGetValue(text, out width))
                return width;

            using (var layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, text, format, 10000f, 1000f))
                width = layout.Metrics.WidthIncludingTrailingWhitespace;

            if (cache.Count > 1000) cache.Clear();
            cache[text] = width;
            return width;
        }

        private float MeasureLineHeight(SharpDX.DirectWrite.TextFormat format)
        {
            float height;
            if (textHeightCache.TryGetValue(format, out height))
                return height;

            using (var layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, "Ag", format, 10000f, 1000f))
                height = layout.Metrics.Height;

            textHeightCache[format] = height;
            return height;
        }

        // Trims text with an ellipsis so it fits the given pixel width
        private string FitText(string text, SharpDX.DirectWrite.TextFormat format, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || MeasureWidth(text, format) <= maxWidth)
                return text;

            const string ellipsis = "\u2026";
            int lo = 0, hi = text.Length;   // binary search for the longest prefix that fits
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                string candidate = text.Substring(0, mid).TrimEnd() + ellipsis;
                if (MeasureWidth(candidate, format) <= maxWidth) lo = mid;
                else hi = mid - 1;
            }

            return lo == 0 ? ellipsis : text.Substring(0, lo).TrimEnd() + ellipsis;
        }
        #endregion

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (RenderTarget == null || chartControl == null || chartScale == null || chartScale.ChartPanel == null) return;

            // Rebuild resources only when the effective theme actually changes (Auto + chart background
            // changed), or if they are missing. Never on an ordinary frame.
            icPANewsTheme currentTheme = ResolveTheme(chartControl);
            if (currentTheme != resolvedTheme || sharpDxTextFormat == null)
            {
                DisposeSharpDxResources();
                resolvedTheme = currentTheme;
                CreateSharpDxResources(RenderTarget);
            }

            lock (instanceLock)
            {
                renderHudList.Clear();
                renderHudList.AddRange(hudEvents);
                renderLineList.Clear();
                renderLineList.AddRange(lineEvents);
            }

            TimeZoneInfo tz = GetConfiguredTimeZone();
            DateTime nowUtc = DateTime.UtcNow;

            // 1. News zones and vertical lines (not on Daily and higher charts)
            renderPanelRight = (float)(chartScale.ChartPanel.X + chartScale.ChartPanel.W);
            if (!dailyOrHigherChart && renderLineList.Count > 0 && (ShowNewsZones || ShowVerticalLines) && PrepareBarsContext(chartControl))
            {
                if (ShowNewsZones)
                    RenderNewsZones(chartControl, chartScale, renderLineList, tz);

                if (ShowVerticalLines)
                    RenderTimelineMarkers(chartControl, chartScale, renderLineList, tz, nowUtc);
            }

            // 2. HUD Panel, drawn last so it sits above our own zones and lines
            if (ShowHUD)
                RenderHUD(chartScale, renderHudList, tz, nowUtc);
            else
                hasHudRect = false;
        }

        #region HUD Interaction & Formatting Helpers
        // Clicking anywhere on the HUD toggles collapsed / expanded
        private void OnChartMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var cc = ChartControl;
            if (!ShowHUD || !hasHudRect || cc == null || State == State.Terminated)
                return;

            // The same click can reach us more than once; toggle only once per click
            if (e.Timestamp == lastToggleTimestamp)
                return;

            bool hit = false;

            // 1. Position relative to this indicator's chart panel. The HUD is drawn in chart
            //    coordinates, so accept a hit with or without the panel offset.
            var panelElement = (object)ChartPanel as System.Windows.IInputElement;
            if (panelElement != null)
            {
                System.Windows.Point p = e.GetPosition(panelElement);

                // Mouse positions are in WPF device-independent units; the HUD is drawn in device pixels
                float px = (float)ChartingExtensions.ConvertToHorizontalPixels(p.X, cc.PresentationSource);
                float py = (float)ChartingExtensions.ConvertToVerticalPixels(p.Y, cc.PresentationSource);
                float ox = (float)ChartPanel.X;
                float oy = (float)ChartPanel.Y;

                hit = HudContains(px, py) || HudContains(px + ox, py + oy);
            }

            // 2. Fallback: position relative to the whole chart control
            if (!hit)
            {
                var controlElement = cc as System.Windows.IInputElement;
                if (controlElement != null)
                {
                    System.Windows.Point p = e.GetPosition(controlElement);
                    float px = (float)ChartingExtensions.ConvertToHorizontalPixels(p.X, cc.PresentationSource);
                    float py = (float)ChartingExtensions.ConvertToVerticalPixels(p.Y, cc.PresentationSource);
                    hit = HudContains(px, py);
                }
            }

            if (hit)
            {
                lastToggleTimestamp = e.Timestamp;
                HudCollapsed = !HudCollapsed;
                e.Handled = true;   // don't let the click select or move anything on the chart
                ForceRefresh();
            }
        }

        private bool HudContains(float px, float py)
        {
            return px >= lastHudRect.Left && px <= lastHudRect.Right && py >= lastHudRect.Top && py <= lastHudRect.Bottom;
        }

        private string FormatClockTime(DateTime localTime)
        {
            return TimeFormat == icPANewsTimeFormat.TwelveHour
                ? localTime.ToString("h:mm tt", CultureInfo.InvariantCulture)
                : localTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        // Calendar day the event belongs to in chart time (all-day events keep the feed's date)
        private static DateTime GetLocalDate(icPANewsEventItem evt, TimeZoneInfo tz)
        {
            return evt.IsAllDay ? evt.FeedDate.Date : TimeZoneInfo.ConvertTimeFromUtc(evt.UtcDateTime, tz).Date;
        }

        private static string DayLabelFor(DateTime date, DateTime today)
        {
            int diff = (int)(date - today).TotalDays;
            if (diff == 0) return "Today";
            if (diff == 1) return "Tomorrow";
            if (diff == -1) return "Yesterday";
            return date.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        }

        // One HUD line: either an event or a day label (struct: no per-frame heap allocation)
        private struct HudRow
        {
            public icPANewsEventItem Event;
            public string DayLabel;
        }

        // One timeline marker; Pinned = upcoming event beyond the right edge, drawn at the edge
        private struct MarkerItem
        {
            public icPANewsEventItem Event;
            public float X;
            public bool Pinned;
        }
        #endregion

        #region Time To Pixel Mapping
        // Loaded bars are needed to map a time to a bar (non-time-based charts) and, on every chart
        // type, to project upcoming times beyond the last bar
        private bool PrepareBarsContext(ChartControl chartControl)
        {
            renderBars = ChartBars != null ? ChartBars.Bars : null;
            if (renderBars == null || renderBars.Count == 0)
                return false;

            int last = renderBars.Count - 1;
            renderFirstBarTime = renderBars.GetTime(0);
            renderLastBarTime = renderBars.GetTime(last);

            // Pixel position of the last bar and the spacing between bars (changes with scroll / zoom).
            // Bar x positions are whole pixels, so a single gap reads 2 or 3 px when the true spacing is
            // e.g. 2.6 px; projected hundreds of bars ahead that error moves a line by tens of minutes.
            // Measuring across many bars makes the spacing accurate to a small fraction of a pixel.
            renderLastBarX = chartControl.GetXByBarIndex(ChartBars, last);
            int spacingSpan = Math.Min(last, 500);
            renderBarDistance = spacingSpan > 0
                ? (renderLastBarX - chartControl.GetXByBarIndex(ChartBars, last - spacingSpan)) / (float)spacingSpan
                : 0f;

            // Seconds per bar only needs recomputing when a new bar is added
            if (renderBars.Count != projectionBarsCount)
            {
                projectionBarsCount = renderBars.Count;
                renderSecondsPerBar = EstimateSecondsPerBar(last);
            }

            return true;
        }

        // Median time between the most recent bars (robust to session breaks). Falls back to the
        // average over the same span when many bars share a timestamp (fast tick bars).
        private double EstimateSecondsPerBar(int last)
        {
            int n = Math.Min(ProjectionSampleBars, last);
            if (n <= 0)
                return 0;

            for (int i = 0; i < n; i++)
            {
                double s = (renderBars.GetTime(last - i) - renderBars.GetTime(last - i - 1)).TotalSeconds;
                barIntervalBuffer[i] = Math.Max(0, s);
            }

            Array.Sort(barIntervalBuffer, 0, n);
            double median = barIntervalBuffer[n / 2];
            if (median > 0)
                return median;

            double span = (renderBars.GetTime(last) - renderBars.GetTime(last - n)).TotalSeconds;
            return span > 0 ? span / n : 1.0;
        }

        // Finds where a future time sits on the chart's own time axis (the same mapping as the time
        // labels and crosshair), by binary search with GetTimeByX between the last bar and the right
        // edge of the panel. Returns the centre of the slot holding that time, a value past the right
        // edge if the time is further ahead than the visible area, or null if the axis can't be read.
        private float? FindFutureXOnTimeAxis(ChartControl chartControl, DateTime localTime)
        {
            try
            {
                int left = (int)Math.Floor(renderLastBarX);
                int right = (int)Math.Ceiling(renderPanelRight);
                if (right <= left)
                    return renderPanelRight + 1f;   // last bar at or past the edge: event is off-screen to the right

                DateTime rightEdgeTime = chartControl.GetTimeByX(right);
                if (rightEdgeTime <= renderLastBarTime)
                    return null;                    // the axis doesn't extend past the last bar: use the projection
                if (rightEdgeTime < localTime)
                    return renderPanelRight + 1f;   // further ahead than the visible area

                int start = FirstXWhere(chartControl, left, right, localTime, false);   // first x with time >= event
                int end = FirstXWhere(chartControl, left, right, localTime, true);      // first x with time >  event

                // Centre of the slot whose time is the event time (or the boundary if the axis is continuous)
                return end > start ? (start + end - 1) / 2f : start;
            }
            catch
            {
                return null;
            }
        }

        // Smallest x in [left, right + 1] where GetTimeByX(x) >= time (or > time when strictlyAfter)
        private static int FirstXWhere(ChartControl chartControl, int left, int right, DateTime time, bool strictlyAfter)
        {
            int lo = left, hi = right + 1;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                DateTime t = chartControl.GetTimeByX(mid);
                bool past = strictlyAfter ? t > time : t >= time;
                if (past) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        // Estimated x for a time after the last bar on a non-time-based chart
        private float? ProjectX(DateTime localTime)
        {
            if (renderBarDistance <= 0f || renderSecondsPerBar <= 0)
                return null;

            double barsAhead = (localTime - renderLastBarTime).TotalSeconds / renderSecondsPerBar;
            double x = renderLastBarX + barsAhead * renderBarDistance;
            return (float)Math.Min(x, 1000000.0);
        }

        // Returns the x position for a chart-local time, or null if it can't be placed on this chart.
        // Non-time-based charts map past times to the bar where they occurred and project times after
        // the last bar forward from the recent bar pace. With clamp = true, times before the first
        // loaded bar snap to that bar instead of returning null.
        private float? TimeToX(ChartControl chartControl, DateTime localTime, bool clamp)
        {
            // Times after the last bar: look the time up on the chart's own time axis, so the line
            // matches the time labels and crosshair on every chart type. If the axis can't be read,
            // fall back to projecting forward from the bar spacing.
            if (localTime > renderLastBarTime)
            {
                float? axisX = FindFutureXOnTimeAxis(chartControl, localTime);
                if (axisX.HasValue)
                    return axisX;
                return ProjectX(localTime);
            }

            if (timeBasedChart)
                return chartControl.GetXByTime(localTime);

            if (localTime < renderFirstBarTime)
            {
                if (!clamp) return null;
                localTime = renderFirstBarTime;
            }

            int barIndex = renderBars.GetBar(localTime);
            if (barIndex < 0)
                return null;

            return chartControl.GetXByBarIndex(ChartBars, barIndex);
        }
        #endregion

        private void RenderNewsZones(ChartControl chartControl, ChartScale chartScale, List<icPANewsEventItem> events, TimeZoneInfo tz)
        {
            float panelLeft = (float)chartScale.ChartPanel.X;
            float panelRight = (float)(chartScale.ChartPanel.X + chartScale.ChartPanel.W);
            float chartTop = (float)chartScale.ChartPanel.Y;
            float chartHeight = (float)chartScale.ChartPanel.H;

            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.IsAllDay)
                    continue;

                DateTime startLocal = TimeZoneInfo.ConvertTimeFromUtc(evt.UtcDateTime.AddMinutes(-NewsWindowMinutesBefore), tz);
                DateTime endLocal = TimeZoneInfo.ConvertTimeFromUtc(evt.UtcDateTime.AddMinutes(NewsWindowMinutesAfter), tz);

                // Non-time-based charts: skip zones that ended before the loaded data
                // (upcoming zones are projected forward and clipped to the panel below)
                if (!timeBasedChart && endLocal < renderFirstBarTime)
                    continue;

                float? xs = TimeToX(chartControl, startLocal, true);
                float? xe = TimeToX(chartControl, endLocal, true);
                if (!xs.HasValue || !xe.HasValue)
                    continue;

                float left = Math.Max(panelLeft, Math.Min(xs.Value, xe.Value));
                float right = Math.Min(panelRight, Math.Max(xs.Value, xe.Value));

                // Make sure even a tiny window is visible as at least a thin band
                if (right - left < 2f)
                    right = left + 2f;
                if (right <= panelLeft || left >= panelRight)
                    continue;

                RenderTarget.FillRectangle(new SharpDX.RectangleF(left, chartTop, right - left, chartHeight), GetZoneBrush(ImpactRank(evt)));
            }
        }

        private void RenderTimelineMarkers(ChartControl chartControl, ChartScale chartScale, List<icPANewsEventItem> events, TimeZoneInfo tz, DateTime nowUtc)
        {
            float panelLeft = (float)chartScale.ChartPanel.X;
            float panelRight = (float)(chartScale.ChartPanel.X + chartScale.ChartPanel.W);
            float chartTop = (float)chartScale.ChartPanel.Y;
            float chartBottom = (float)(chartScale.ChartPanel.Y + chartScale.ChartPanel.H);

            // Upcoming events beyond the right edge are pinned here (only within the HUD look-ahead,
            // so a whole week of events doesn't pile up on the edge)
            float pinX = panelRight - Math.Max(2f, LineThickness);
            DateTime pinLimitUtc = nowUtc.AddMinutes(LookforwardMinutes);

            // Resolve x position for each visible event
            var visible = renderVisible;
            visible.Clear();
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.IsAllDay)
                    continue;   // a line at midnight would be misleading

                float? x = TimeToX(chartControl, TimeZoneInfo.ConvertTimeFromUtc(evt.UtcDateTime, tz), false);
                bool pinned = false;

                if (!x.HasValue || x.Value > panelRight)
                {
                    if (evt.UtcDateTime > nowUtc && evt.UtcDateTime <= pinLimitUtc)
                    {
                        x = pinX;
                        pinned = true;
                    }
                    else
                    {
                        continue;
                    }
                }

                if (x.Value < panelLeft)
                    continue;

                visible.Add(new MarkerItem { Event = evt, X = x.Value, Pinned = pinned });
            }

            if (visible.Count == 0) return;

            // Lines: one per x position, colored by the most important event at that position
            renderTopRankByX.Clear();
            for (int i = 0; i < visible.Count; i++)
            {
                int x = (int)Math.Round(visible[i].X);
                int rank = ImpactRank(visible[i].Event);
                int existing;
                if (!renderTopRankByX.TryGetValue(x, out existing) || rank > existing)
                    renderTopRankByX[x] = rank;
            }

            foreach (var line in renderTopRankByX)
                RenderTarget.DrawLine(new SharpDX.Vector2(line.Key, chartTop), new SharpDX.Vector2(line.Key, chartBottom), GetImpactBrush(line.Value), LineThickness, sharpDxStrokeStyle);

            // Labels: stack downwards whenever a label would overlap one already placed
            // (covers both identical times and events close enough that the text collides).
            float labelHeight = (float)Math.Ceiling(MeasureLineHeight(sharpDxSmallFormat) + 4f);
            float labelSpacing = 2f;
            float maxTitleWidth = sharpDxSmallFormat.FontSize * 12f;
            var placed = renderPlacedLabels;
            placed.Clear();

            for (int i = 0; i < visible.Count; i++)
            {
                var evt = visible[i].Event;
                float lineX = (float)Math.Round(visible[i].X);

                // Pinned markers don't sit at their real time, so the label carries the time and countdown
                string tag = visible[i].Pinned
                    ? "\u25B8 " + FormatClockTime(TimeZoneInfo.ConvertTimeFromUtc(evt.UtcDateTime, tz)) + " " + evt.Country + " "
                        + FitText(evt.Title, sharpDxSmallFormat, maxTitleWidth) + " (" + FormatCountdown(evt, nowUtc) + ")"
                    : evt.Country + " " + FitText(evt.Title, sharpDxSmallFormat, maxTitleWidth);
                float textWidth = MeasureWidth(tag, sharpDxSmallFormat);

                float labelWidth = textWidth + 8f;
                float labelX = lineX + 4f;

                // Flip to the left of the line if it would run off the right edge of the panel
                if (labelX + labelWidth > panelRight)
                    labelX = lineX - 4f - labelWidth;

                int slot = 0;
                SharpDX.RectangleF rect;
                while (true)
                {
                    rect = new SharpDX.RectangleF(labelX, chartTop + 6f + slot * (labelHeight + labelSpacing), labelWidth, labelHeight);
                    bool overlaps = false;
                    for (int j = 0; j < placed.Count; j++)
                    {
                        var p = placed[j];
                        if (rect.Left < p.Right && rect.Right > p.Left && rect.Top < p.Bottom && rect.Bottom > p.Top)
                        {
                            overlaps = true;
                            break;
                        }
                    }
                    if (!overlaps) break;
                    slot++;
                }
                placed.Add(rect);

                // Subtle background so labels stay readable over price bars
                RenderTarget.FillRectangle(rect, sharpDxHudBgBrush);
                RenderTarget.DrawText(tag, sharpDxSmallFormat, new SharpDX.RectangleF(rect.X + 4f, rect.Y, textWidth + 4f, labelHeight), GetImpactBrush(ImpactRank(evt)));
            }
        }

        private static string FormatCountdown(icPANewsEventItem evt, DateTime nowUtc)
        {
            // All-day events: the time column already says "All day" and the day label says which day
            if (evt.IsAllDay)
                return string.Empty;

            TimeSpan diff = evt.UtcDateTime - nowUtc;

            if (Math.Abs(diff.TotalMinutes) < 1)
                return "NOW";

            if (diff.TotalMinutes < 0)
            {
                int ago = (int)Math.Abs(diff.TotalMinutes);
                return ago >= 60
                    ? string.Format("{0}h {1}m ago", ago / 60, ago % 60)
                    : string.Format("{0}m ago", ago);
            }

            if (diff.TotalMinutes >= 60)
                return string.Format("in {0}h {1}m", (int)diff.TotalHours, diff.Minutes);

            return string.Format("in {0}m", (int)diff.TotalMinutes);
        }

        private void RenderHUD(ChartScale chartScale, List<icPANewsEventItem> events, TimeZoneInfo tz, DateTime nowUtc)
        {
            const float margin = 10f;
            const float pad = 10f;
            const float gap = 8f;

            float areaX = (float)chartScale.ChartPanel.X;
            float areaY = (float)chartScale.ChartPanel.Y;
            float areaW = (float)chartScale.ChartPanel.W;
            float areaH = (float)chartScale.ChartPanel.H;

            float lineHeight = MeasureLineHeight(sharpDxBoldFormat);
            float rowHeight = (float)Math.Ceiling(lineHeight + 6f);
            float headerHeight = (float)Math.Ceiling(lineHeight + 10f);

            // How many rows fit: the setting, but never taller than the chart panel
            int fitRows = (int)Math.Floor((areaH - 2 * margin - HudOffsetY - headerHeight - pad) / rowHeight);
            int maxRows = Math.Max(1, Math.Min(MaxHudRows, fitRows));

            DateTime today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz).Date;
            var rows = renderRows;
            rows.Clear();
            int hiddenCount = 0;

            icPANewsEventItem collapsedNext = null;

            if (HudCollapsed)
            {
                // Collapsed: header line only. The header names the next timed event and its countdown.
                for (int i = 0; i < events.Count; i++)
                {
                    var evt = events[i];
                    if (!evt.IsAllDay && evt.UtcDateTime >= nowUtc.AddMinutes(-1))
                    {
                        collapsedNext = evt;
                        break;
                    }
                }
            }
            else
            {
                // Day labels only when the list spans more than one day
                bool multiDay = false;
                for (int i = 0; i < events.Count; i++)
                {
                    if (GetLocalDate(events[i], tz) != today)
                    {
                        multiDay = true;
                        break;
                    }
                }

                DateTime? lastDate = null;

                for (int i = 0; i < events.Count; i++)
                {
                    var evt = events[i];
                    DateTime d = GetLocalDate(evt, tz);
                    bool needsLabel = multiDay && (!lastDate.HasValue || d != lastDate.Value);
                    int needed = needsLabel ? 2 : 1;
                    int reserve = i < events.Count - 1 ? 1 : 0;   // room for "+N more" if more follow

                    if (rows.Count > 0 && rows.Count + needed + reserve > maxRows)
                    {
                        hiddenCount = events.Count - i;
                        break;
                    }

                    if (needsLabel)
                        rows.Add(new HudRow { DayLabel = DayLabelFor(d, today) });
                    rows.Add(new HudRow { Event = evt });
                    lastDate = d;
                }
            }

            // Column widths (fixed baselines so the panel doesn't jitter as countdowns change)
            string timeSample = TimeFormat == icPANewsTimeFormat.TwelveHour ? "12:00 PM" : "00:00";
            float timeW = Math.Max(MeasureWidth(timeSample, sharpDxBoldFormat), MeasureWidth("All day", sharpDxBoldFormat));
            float countdownW = Math.Max(MeasureWidth("in 23h 59m", sharpDxSmallFormat), MeasureWidth("23h 59m ago", sharpDxSmallFormat));
            float countryW = MeasureWidth("WWW", sharpDxBoldFormat);

            float minTitleW = sharpDxTextFormat.FontSize * 8f;
            float maxTitleW = sharpDxTextFormat.FontSize * 22f;
            float titleW = minTitleW;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Event != null)
                    titleW = Math.Max(titleW, MeasureWidth(rows[i].Event.Title, sharpDxTextFormat));
            titleW = Math.Min(titleW, maxTitleW);

            string headerText = HudCollapsed ? headerTextCollapsed : headerTextExpanded;
            if (headerText == null)
                headerText = string.Format("{0} ECONOMIC CALENDAR ({1})", HudCollapsed ? "\u25B8" : "\u25BE", currencyHeader);
            if (collapsedNext != null)
                headerText = headerText + "  \u00B7  next " + FormatClockTime(TimeZoneInfo.ConvertTimeFromUtc(collapsedNext.UtcDateTime, tz))
                    + " " + collapsedNext.Country + " (" + FormatCountdown(collapsedNext, nowUtc) + ")";
            const string creditText = "via Forex Factory";
            const string emptyText = "No events in the current window";

            float headerTextW = MeasureWidth(headerText, sharpDxBoldFormat);
            float creditW = MeasureWidth(creditText, sharpDxSmallFormat);

            float contentW = HudCollapsed ? 0f : events.Count > 0
                ? timeW + gap + countdownW + gap + countryW + gap + titleW
                : MeasureWidth(emptyText, sharpDxSmallFormat);

            float panelWidth = 2 * pad + Math.Max(contentW, headerTextW + 2 * gap + creditW);

            // Never wider than the chart: shrink the title column first
            float maxPanelW = areaW - 2 * margin;
            if (panelWidth > maxPanelW)
            {
                float excess = panelWidth - maxPanelW;
                titleW = Math.Max(30f, titleW - excess);
                panelWidth = maxPanelW;
            }

            bool showCredit = headerTextW + 2 * gap + creditW <= panelWidth - 2 * pad;

            // Collapsed with nothing to show: header line only
            int lineCount = rows.Count == 0
                ? (HudCollapsed ? 0 : 1)
                : rows.Count + (hiddenCount > 0 ? 1 : 0);
            float panelHeight = headerHeight + lineCount * rowHeight + 6f;

            // Offsets always move the panel inwards from its docked corner
            float marginX = margin + HudOffsetX;
            float marginY = margin + HudOffsetY;

            float x = areaX + marginX;
            float y = areaY + marginY;

            switch (CornerPosition)
            {
                case TextPosition.TopRight:
                    x = areaX + areaW - panelWidth - marginX;
                    break;
                case TextPosition.BottomLeft:
                    y = areaY + areaH - panelHeight - marginY;
                    break;
                case TextPosition.BottomRight:
                    x = areaX + areaW - panelWidth - marginX;
                    y = areaY + areaH - panelHeight - marginY;
                    break;
            }

            // Keep the panel inside the chart even with large offsets
            x = Math.Max(areaX, Math.Min(x, areaX + areaW - panelWidth));
            y = Math.Max(areaY, Math.Min(y, areaY + areaH - panelHeight));

            var panelRect = new SharpDX.Direct2D1.RoundedRectangle
            {
                Rect = new SharpDX.RectangleF(x, y, panelWidth, panelHeight),
                RadiusX = 6f,
                RadiusY = 6f
            };

            // Remember where the HUD is for click-to-collapse
            lastHudRect = panelRect.Rect;
            hasHudRect = true;

            RenderTarget.FillRoundedRectangle(panelRect, sharpDxHudBgBrush);
            RenderTarget.DrawRoundedRectangle(panelRect, sharpDxHudBorderBrush, 1.2f);

            // Header
            RenderTarget.DrawText(headerText, sharpDxBoldFormat, new SharpDX.RectangleF(x + pad, y, panelWidth - 2 * pad, headerHeight), sharpDxTextBrush);
            if (showCredit)
                RenderTarget.DrawText(creditText, sharpDxSmallFormat, new SharpDX.RectangleF(x + panelWidth - pad - creditW, y, creditW + 2f, headerHeight), sharpDxDimTextBrush);

            if (lineCount > 0)
                RenderTarget.DrawLine(new SharpDX.Vector2(x + pad, y + headerHeight), new SharpDX.Vector2(x + panelWidth - pad, y + headerHeight), sharpDxHudBorderBrush, 1f);

            float currentY = y + headerHeight + 3f;

            if (rows.Count == 0)
            {
                if (!HudCollapsed)
                    RenderTarget.DrawText(emptyText, sharpDxSmallFormat, new SharpDX.RectangleF(x + pad, currentY, panelWidth - 2 * pad, rowHeight), sharpDxDimTextBrush);
                return;
            }

            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                if (row.Event == null)
                {
                    // Day label
                    RenderTarget.DrawText(row.DayLabel, sharpDxSmallFormat, new SharpDX.RectangleF(x + pad, currentY, panelWidth - 2 * pad, rowHeight), sharpDxDimTextBrush);
                    currentY += rowHeight;
                    continue;
                }

                var evt = row.Event;
                int rank = ImpactRank(evt);
                bool past = DimPastEvents && !evt.IsAllDay && evt.UtcDateTime < nowUtc.AddMinutes(-1);
                bool minor = rank <= 1 || past;   // low impact, holidays and past events are drawn dimmed

                string timeStr = evt.IsAllDay
                    ? "All day"
                    : FormatClockTime(TimeZoneInfo.ConvertTimeFromUtc(evt.UtcDateTime, tz));

                float cx = x + pad;
                RenderTarget.DrawText(timeStr, sharpDxBoldFormat, new SharpDX.RectangleF(cx, currentY, timeW + 2f, rowHeight), minor ? sharpDxDimTextBrush : sharpDxTextBrush);
                cx += timeW + gap;

                RenderTarget.DrawText(FormatCountdown(evt, nowUtc), sharpDxSmallFormat, new SharpDX.RectangleF(cx, currentY, countdownW + 2f, rowHeight), sharpDxDimTextBrush);
                cx += countdownW + gap;

                RenderTarget.DrawText(evt.Country, sharpDxBoldFormat, new SharpDX.RectangleF(cx, currentY, countryW + 2f, rowHeight), past ? sharpDxDimTextBrush : GetImpactBrush(rank));
                cx += countryW + gap;

                string title = FitText(evt.Title, sharpDxTextFormat, titleW);
                RenderTarget.DrawText(title, sharpDxTextFormat, new SharpDX.RectangleF(cx, currentY, titleW + 2f, rowHeight), minor ? sharpDxDimTextBrush : sharpDxTextBrush);

                currentY += rowHeight;
            }

            if (hiddenCount > 0)
            {
                string moreText = string.Format("+{0} more", hiddenCount);
                RenderTarget.DrawText(moreText, sharpDxSmallFormat, new SharpDX.RectangleF(x + pad, currentY, panelWidth - 2 * pad, rowHeight), sharpDxDimTextBrush);
            }
        }
        #endregion
    }

    #region Properties Dialog Type Converter
    // Shows the color pickers only when Theme = Custom, keeping the dialog clean for everyone else.
    public class icPANewsTypeConverter : IndicatorBaseConverter
    {
        private static readonly HashSet<string> CustomOnlyProperties = new HashSet<string>
        {
            "HudBackgroundBrush",
            "HudBorderBrush",
            "HudTextBrush",
            "HudDimTextBrush",
            "HighImpactColor",
            "MedImpactColor"
        };

        public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object component, Attribute[] attrs)
        {
            PropertyDescriptorCollection all = base.GetPropertiesSupported(context)
                ? base.GetProperties(context, component, attrs)
                : TypeDescriptor.GetProperties(component, attrs);

            icPANews indicator = component as icPANews;
            if (indicator == null || all == null || indicator.Theme == icPANewsTheme.Custom)
                return all;

            var filtered = new PropertyDescriptorCollection(null);
            foreach (PropertyDescriptor pd in all)
            {
                if (!CustomOnlyProperties.Contains(pd.Name))
                    filtered.Add(pd);
            }
            return filtered;
        }

        public override bool GetPropertiesSupported(ITypeDescriptorContext context)
        {
            return true;
        }
    }
    #endregion
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private icPA.icPANews[] cacheicPANews;
		public icPA.icPANews icPANews()
		{
			return icPANews(Input);
		}

		public icPA.icPANews icPANews(ISeries<double> input)
		{
			if (cacheicPANews != null)
				for (int idx = 0; idx < cacheicPANews.Length; idx++)
					if (cacheicPANews[idx] != null &&  cacheicPANews[idx].EqualsInput(input))
						return cacheicPANews[idx];
			return CacheIndicator<icPA.icPANews>(new icPA.icPANews(), input, ref cacheicPANews);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.icPA.icPANews icPANews()
		{
			return indicator.icPANews(Input);
		}

		public Indicators.icPA.icPANews icPANews(ISeries<double> input )
		{
			return indicator.icPANews(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.icPA.icPANews icPANews()
		{
			return indicator.icPANews(Input);
		}

		public Indicators.icPA.icPANews icPANews(ISeries<double> input )
		{
			return indicator.icPANews(input);
		}
	}
}

#endregion