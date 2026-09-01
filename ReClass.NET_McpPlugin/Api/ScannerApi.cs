using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using McpPlugin.Rpc;
using ReClassNET;
using ReClassNET.MemoryScanner;
using ReClassNET.MemoryScanner.Comparer;

namespace McpPlugin.Api
{
	/// <summary>
	/// The memory scanner. Owns the single active <see cref="Scanner"/>, its
	/// <see cref="CancellationTokenSource"/> and the progress counter; all of
	/// them are guarded by <see cref="sync"/>.
	///
	/// The scan itself runs on a worker (<see cref="Scanner.Search"/> returns a
	/// task), so <c>scan.first</c> / <c>scan.next</c> return immediately and the
	/// caller polls <c>scan.status</c>. A second scan while one is running is
	/// rejected with <c>-32006 busy</c>.
	/// </summary>
	public class ScannerApi : IDisposable
	{
		/// <summary>Never return more than this many results in one response.</summary>
		public const int MaxResultLimit = 10000;

		private readonly object sync = new object();

		private Scanner scanner;
		private CancellationTokenSource cancellation;
		private int progress;
		private bool running;
		private int job;

		public void Register(RpcDispatcher dispatcher)
		{
			dispatcher.Register("scan.first", First);
			dispatcher.Register("scan.next", Next);
			dispatcher.Register("scan.status", Status);
			dispatcher.Register("scan.results", Results);
			dispatcher.Register("scan.undo", Undo);
			dispatcher.Register("scan.cancel", Cancel);
			dispatcher.Register("scan.reset", Reset);
		}

		public void Dispose()
		{
			lock (sync)
			{
				DisposeScannerLocked();
			}
		}

		private void DisposeScannerLocked()
		{
			try
			{
				cancellation?.Cancel();
			}
			catch (Exception)
			{
				// ignored
			}

			try
			{
				scanner?.Dispose();
			}
			catch (Exception)
			{
				// ignored
			}

			cancellation?.Dispose();
			cancellation = null;
			scanner = null;
			running = false;
			progress = 0;
		}

		// ------------------------------------------------------------------
		// Settings and comparer construction
		// ------------------------------------------------------------------

		private static readonly Dictionary<string, ScanValueType> ValueTypes = new Dictionary<string, ScanValueType>(StringComparer.OrdinalIgnoreCase)
		{
			{ "byte", ScanValueType.Byte },
			{ "short", ScanValueType.Short },
			{ "integer", ScanValueType.Integer },
			{ "int", ScanValueType.Integer },
			{ "long", ScanValueType.Long },
			{ "float", ScanValueType.Float },
			{ "double", ScanValueType.Double },
			{ "bytes", ScanValueType.ArrayOfBytes },
			{ "array_of_bytes", ScanValueType.ArrayOfBytes },
			{ "string", ScanValueType.String },
			{ "regex", ScanValueType.Regex }
		};

		private static ScanValueType ParseValueType(string name)
		{
			if (name != null && ValueTypes.TryGetValue(name.Trim(), out var valueType))
			{
				return valueType;
			}
			throw RpcException.BadAddress($"unknown value type '{name}', expected one of {string.Join(", ", ValueTypes.Keys)}");
		}

		private static ScanCompareType ParseCompareType(string name)
		{
			var normalized = (name ?? string.Empty).Replace("_", string.Empty).Trim();
			foreach (ScanCompareType value in Enum.GetValues(typeof(ScanCompareType)))
			{
				if (string.Equals(value.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
				{
					return value;
				}
			}
			throw RpcException.BadAddress($"unknown compare type '{name}'");
		}

		/// <summary>Reads a tri-state setting: <c>"yes"|"no"|"indeterminate"</c> or a bool.</summary>
		private static SettingState ParseSettingState(Dictionary<string, object> settings, string name, SettingState defaultValue)
		{
			if (!Params.Has(settings, name))
			{
				return defaultValue;
			}

			var value = settings[name];
			if (value is bool flag)
			{
				return flag ? SettingState.Yes : SettingState.No;
			}

			var text = System.Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
			switch (text.ToLowerInvariant())
			{
				case "yes":
				case "true":
					return SettingState.Yes;
				case "no":
				case "false":
					return SettingState.No;
				case "indeterminate":
				case "any":
					return SettingState.Indeterminate;
				default:
					throw RpcException.BadAddress($"'{name}' must be \"yes\", \"no\" or \"indeterminate\"");
			}
		}

		private static ScanSettings BuildSettings(Dictionary<string, object> p, ScanValueType valueType)
		{
			var settings = ScanSettings.Default;
			settings.ValueType = valueType;

			if (!Params.Has(p, "settings"))
			{
				return settings;
			}

			var s = Params.AsObject(p["settings"], "settings");

			if (Params.Has(s, "start"))
			{
				settings.StartAddress = Params.GetAddress(s, "start");
			}
			if (Params.Has(s, "stop"))
			{
				settings.StopAddress = Params.GetAddress(s, "stop");
			}
			if (Params.Has(s, "alignment"))
			{
				var alignment = Params.Get<int>(s, "alignment");
				if (alignment < 1)
				{
					throw RpcException.BadAddress("'alignment' must be at least 1");
				}
				settings.FastScanAlignment = alignment;
			}

			settings.EnableFastScan = Params.GetOptional(s, "fast", settings.EnableFastScan);
			settings.ScanPrivateMemory = Params.GetOptional(s, "private", settings.ScanPrivateMemory);
			settings.ScanImageMemory = Params.GetOptional(s, "image", settings.ScanImageMemory);
			settings.ScanMappedMemory = Params.GetOptional(s, "mapped", settings.ScanMappedMemory);

			settings.ScanWritableMemory = ParseSettingState(s, "writable", settings.ScanWritableMemory);
			settings.ScanExecutableMemory = ParseSettingState(s, "executable", settings.ScanExecutableMemory);
			settings.ScanCopyOnWriteMemory = ParseSettingState(s, "cow", settings.ScanCopyOnWriteMemory);

			if (settings.StopAddress.ToInt64() <= settings.StartAddress.ToInt64())
			{
				throw RpcException.BadAddress("'stop' must be greater than 'start'");
			}

			return settings;
		}

		private static long ParseInteger(object value, string name)
		{
			if (value is string text)
			{
				text = text.Trim();
				var negative = text.StartsWith("-", StringComparison.Ordinal);
				if (negative)
				{
					text = text.Substring(1).TrimStart();
				}

				if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				{
					if (!ulong.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
					{
						throw RpcException.BadAddress($"'{name}' is not a valid integer");
					}
					var signed = unchecked((long)hex);
					return negative ? -signed : signed;
				}

				if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
				{
					// Accept unsigned values which do not fit into a long, e.g. 0xFEEDFACF written as 4277009103.
					if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
					{
						return unchecked((long)unsigned);
					}
					throw RpcException.BadAddress($"'{name}' is not a valid integer");
				}
				return negative ? -parsed : parsed;
			}

			try
			{
				if (value is double d)
				{
					// JavaScriptSerializer produces doubles for large numbers.
					return (long)d;
				}
				return System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
			}
			catch (OverflowException)
			{
				return unchecked((long)System.Convert.ToUInt64(value, CultureInfo.InvariantCulture));
			}
			catch (Exception)
			{
				throw RpcException.BadAddress($"'{name}' is not a valid integer");
			}
		}

		private static double ParseFloat(object value, string name)
		{
			if (value is string text)
			{
				if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
				{
					throw RpcException.BadAddress($"'{name}' is not a valid number");
				}
				return parsed;
			}

			try
			{
				return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
			}
			catch (Exception)
			{
				throw RpcException.BadAddress($"'{name}' is not a valid number");
			}
		}

		private static Encoding ParseEncoding(Dictionary<string, object> p)
		{
			var encoding = Params.GetOptional(p, "encoding", "utf8");
			switch ((encoding ?? string.Empty).Trim().ToLowerInvariant())
			{
				case "utf8":
					return Encoding.UTF8;
				case "utf16":
				case "unicode":
					return Encoding.Unicode;
				case "utf32":
					return Encoding.UTF32;
				default:
					throw RpcException.BadAddress("'encoding' must be utf8, utf16 or utf32");
			}
		}

		/// <summary>
		/// Builds the comparer, mirroring <c>ScannerForm.CreateComparer</c>. The
		/// comparers implement both the first-scan (<c>Compare(data, index, out
		/// result)</c>) and the next-scan (<c>Compare(data, index, previous, out
		/// result)</c>) overloads, so the same object serves both scan kinds and
		/// the previous-value compare types (<c>Changed</c>, <c>Increased</c>, …)
		/// need no extra state here.
		/// </summary>
		private static IScanComparer CreateComparer(Dictionary<string, object> p, ScanValueType valueType, ScanCompareType compareType)
		{
			var bitConverter = Program.RemoteProcess.BitConverter;

			var needsValue = compareType != ScanCompareType.Unknown
			                 && compareType != ScanCompareType.Changed
			                 && compareType != ScanCompareType.NotChanged
			                 && compareType != ScanCompareType.Increased
			                 && compareType != ScanCompareType.IncreasedOrEqual
			                 && compareType != ScanCompareType.Decreased
			                 && compareType != ScanCompareType.DecreasedOrEqual;

			var needsSecondValue = compareType == ScanCompareType.Between || compareType == ScanCompareType.BetweenOrEqual;

			switch (valueType)
			{
				case ScanValueType.Byte:
				case ScanValueType.Short:
				case ScanValueType.Integer:
				case ScanValueType.Long:
				{
					long value1 = 0;
					long value2 = 0;
					if (needsValue || Params.Has(p, "value"))
					{
						value1 = ParseInteger(Params.GetRaw(p, "value"), "value");
					}
					if (Params.Has(p, "value2"))
					{
						value2 = ParseInteger(p["value2"], "value2");
					}
					else if (needsSecondValue)
					{
						throw RpcException.BadAddress($"compare type '{compareType}' requires 'value2'");
					}

					if (needsSecondValue && value1 > value2)
					{
						var temp = value1;
						value1 = value2;
						value2 = temp;
					}

					switch (valueType)
					{
						case ScanValueType.Byte:
							return new ByteMemoryComparer(compareType, unchecked((byte)value1), unchecked((byte)value2));
						case ScanValueType.Short:
							return new ShortMemoryComparer(compareType, unchecked((short)value1), unchecked((short)value2), bitConverter);
						case ScanValueType.Integer:
							return new IntegerMemoryComparer(compareType, unchecked((int)value1), unchecked((int)value2), bitConverter);
						default:
							return new LongMemoryComparer(compareType, value1, value2, bitConverter);
					}
				}

				case ScanValueType.Float:
				case ScanValueType.Double:
				{
					double value1 = 0;
					double value2 = 0;
					if (needsValue || Params.Has(p, "value"))
					{
						value1 = ParseFloat(Params.GetRaw(p, "value"), "value");
					}
					if (Params.Has(p, "value2"))
					{
						value2 = ParseFloat(p["value2"], "value2");
					}
					else if (needsSecondValue)
					{
						throw RpcException.BadAddress($"compare type '{compareType}' requires 'value2'");
					}

					if (needsSecondValue && value1 > value2)
					{
						var temp = value1;
						value1 = value2;
						value2 = temp;
					}

					// The form derives the significant digits from the typed
					// text; over the RPC we compare on the full precision of the
					// given number instead (ScanRoundMode.Normal, 6 digits).
					var roundMode = ParseRoundMode(Params.GetOptional(p, "round", "normal"));
					var digits = Params.GetOptional(p, "significant_digits", 6);

					return valueType == ScanValueType.Float
						? (IScanComparer)new FloatMemoryComparer(compareType, roundMode, digits, (float)value1, (float)value2, bitConverter)
						: new DoubleMemoryComparer(compareType, roundMode, digits, value1, value2, bitConverter);
				}

				case ScanValueType.ArrayOfBytes:
				{
					var pattern = Params.Get<string>(p, "value");
					try
					{
						return new ArrayOfBytesMemoryComparer(BytePattern.Parse(pattern));
					}
					catch (Exception ex)
					{
						throw RpcException.BadAddress($"'value' is not a valid byte pattern: {ex.Message}");
					}
				}

				case ScanValueType.String:
				{
					var value = Params.Get<string>(p, "value");
					if (string.IsNullOrEmpty(value))
					{
						throw RpcException.BadAddress("'value' must not be empty");
					}
					return new StringMemoryComparer(value, ParseEncoding(p), Params.GetOptional(p, "case_sensitive", true));
				}

				default:
				{
					var value = Params.Get<string>(p, "value");
					if (string.IsNullOrEmpty(value))
					{
						throw RpcException.BadAddress("'value' must not be empty");
					}
					try
					{
						return new RegexStringMemoryComparer(value, ParseEncoding(p), Params.GetOptional(p, "case_sensitive", true));
					}
					catch (ArgumentException ex)
					{
						throw RpcException.BadAddress($"'value' is not a valid regular expression: {ex.Message}");
					}
				}
			}
		}

		private static ScanRoundMode ParseRoundMode(string name)
		{
			switch ((name ?? string.Empty).Trim().ToLowerInvariant())
			{
				case "strict":
					return ScanRoundMode.Strict;
				case "normal":
				case "loose":
					return ScanRoundMode.Normal;
				case "truncate":
					return ScanRoundMode.Truncate;
				default:
					throw RpcException.BadAddress("'round' must be strict, normal or truncate");
			}
		}

		// ------------------------------------------------------------------
		// Methods
		// ------------------------------------------------------------------

		private object First(Dictionary<string, object> p)
		{
			MemoryApi.RequireProcess();

			var valueType = ParseValueType(Params.Get<string>(p, "value_type"));
			var compareType = ParseCompareType(Params.Get<string>(p, "compare"));
			var settings = BuildSettings(p, valueType);
			var comparer = CreateComparer(p, valueType, compareType);

			lock (sync)
			{
				if (running)
				{
					throw RpcException.Busy("a scan is already running");
				}

				DisposeScannerLocked();

				scanner = new Scanner(Program.RemoteProcess, settings);

				return StartLocked(comparer);
			}
		}

		private object Next(Dictionary<string, object> p)
		{
			MemoryApi.RequireProcess();

			lock (sync)
			{
				if (running)
				{
					throw RpcException.Busy("a scan is already running");
				}
				if (scanner == null)
				{
					throw RpcException.NotFound("no scan is active, call scan.first first");
				}

				var compareType = ParseCompareType(Params.Get<string>(p, "compare"));
				var comparer = CreateComparer(p, scanner.Settings.ValueType, compareType);

				return StartLocked(comparer);
			}
		}

		/// <summary>Starts the search on a worker. Must be called under <see cref="sync"/>.</summary>
		private object StartLocked(IScanComparer comparer)
		{
			cancellation?.Dispose();
			cancellation = new CancellationTokenSource();

			progress = 0;
			running = true;
			var current = ++job;

			var token = cancellation.Token;
			var currentScanner = scanner;

			// Scanner.Search itself dispatches the work to a task; awaiting the
			// returned task on a worker keeps the RPC thread free and lets us
			// clear the running flag when it finishes, faults or is cancelled.
			System.Threading.Tasks.Task.Run(async () =>
			{
				try
				{
					await currentScanner.Search(comparer, new Progress<int>(value =>
					{
						lock (sync)
						{
							progress = value;
						}
					}), token).ConfigureAwait(false);
				}
				catch (Exception)
				{
					// A cancelled or failed scan simply ends the job; the error
					// surfaces as an unchanged result count.
				}
				finally
				{
					lock (sync)
					{
						if (job == current)
						{
							running = false;
							progress = 100;
						}
					}
				}
			});

			return new Dictionary<string, object> { { "job", current } };
		}

		private object Status(Dictionary<string, object> p)
		{
			lock (sync)
			{
				return new Dictionary<string, object>
				{
					{ "running", running },
					{ "progress", progress },
					{ "total", scanner?.TotalResultCount ?? 0 },
					{ "job", job }
				};
			}
		}

		private object Results(Dictionary<string, object> p)
		{
			var offset = Params.GetOptional(p, "offset", 0);
			var limit = Params.GetOptional(p, "limit", 1000);

			if (offset < 0)
			{
				throw RpcException.BadAddress("'offset' must not be negative");
			}
			if (limit < 0 || limit > MaxResultLimit)
			{
				throw RpcException.BadAddress($"'limit' must be between 0 and {MaxResultLimit}");
			}

			lock (sync)
			{
				if (scanner == null)
				{
					throw RpcException.NotFound("no scan is active, call scan.first first");
				}

				var results = scanner.GetResults()
					.Skip(offset)
					.Take(limit)
					.Select(r => (object)new Dictionary<string, object>
					{
						{ "address", Json.Address(r.Address) },
						{ "value", DescribeValue(r) }
					})
					.ToList();

				return new Dictionary<string, object>
				{
					{ "total", scanner.TotalResultCount },
					{ "results", results }
				};
			}
		}

		/// <summary>
		/// The scan result value, typed per result class: numbers stay numbers
		/// (64 bit values become strings, as everywhere else in this API), byte
		/// arrays become an upper case hex string and strings stay strings.
		/// </summary>
		private static object DescribeValue(ScanResult result)
		{
			switch (result)
			{
				case ByteScanResult r:
					return (int)r.Value;
				case ShortScanResult r:
					return (int)r.Value;
				case IntegerScanResult r:
					return r.Value;
				case LongScanResult r:
					return r.Value.ToString(CultureInfo.InvariantCulture);
				case FloatScanResult r:
					return r.Value;
				case DoubleScanResult r:
					return r.Value;
				case ArrayOfBytesScanResult r:
					return string.Join(" ", r.Value.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
				case StringScanResult r:
					return r.Value;
				default:
					return null;
			}
		}

		private object Undo(Dictionary<string, object> p)
		{
			lock (sync)
			{
				if (scanner == null)
				{
					throw RpcException.NotFound("no scan is active, call scan.first first");
				}
				if (running)
				{
					throw RpcException.Busy("a scan is running");
				}
				if (!scanner.CanUndoLastScan)
				{
					throw RpcException.BadAddress("there is no scan to undo");
				}

				scanner.UndoLastScan();

				return new Dictionary<string, object>
				{
					{ "ok", true },
					{ "total", scanner.TotalResultCount }
				};
			}
		}

		private object Cancel(Dictionary<string, object> p)
		{
			lock (sync)
			{
				try
				{
					cancellation?.Cancel();
				}
				catch (Exception)
				{
					// ignored
				}
			}

			return ProcessApi.Ok();
		}

		private object Reset(Dictionary<string, object> p)
		{
			lock (sync)
			{
				DisposeScannerLocked();
			}

			return ProcessApi.Ok();
		}
	}
}
