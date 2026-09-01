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

		/// <summary>Serializes the lifecycle operations (start / cancel / dispose).</summary>
		private readonly object gate = new object();

		/// <summary>Guards the mutable state below; never held while waiting on the worker.</summary>
		private readonly object sync = new object();

		private Scanner scanner;
		private CancellationTokenSource cancellation;
		private System.Threading.Tasks.Task worker;
		private int progress;
		private bool running;
		private int job;

		/// <summary>The message of the exception the last worker failed with, or null.</summary>
		private string lastError;

		/// <summary>The return value of the last finished <see cref="Scanner.Search"/>, or null.</summary>
		private bool? lastSuccess;

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
			lock (gate)
			{
				DisposeScanner();
			}
		}

		/// <summary>
		/// Cancels the running worker and waits (bounded) for it to finish.
		/// Must NOT be called while <see cref="sync"/> is held: the worker's
		/// completion handler takes that lock, so waiting under it would deadlock.
		/// </summary>
		private void CancelAndWaitForWorker()
		{
			CancellationTokenSource cts;
			System.Threading.Tasks.Task task;
			lock (sync)
			{
				cts = cancellation;
				task = worker;
			}

			try
			{
				cts?.Cancel();
			}
			catch (Exception)
			{
				// ignored
			}

			if (task != null)
			{
				try
				{
					task.Wait(TimeSpan.FromSeconds(10));
				}
				catch (Exception)
				{
					// the worker swallows its own errors; a timeout simply means the
					// scan did not stop in time.
				}
			}
		}

		/// <summary>
		/// Stops the worker and disposes the scanner. Callers hold
		/// <see cref="gate"/>, never <see cref="sync"/>, so that the bounded wait
		/// inside cannot deadlock against the worker's completion handler.
		/// </summary>
		private void DisposeScanner()
		{
			CancelAndWaitForWorker();

			Scanner oldScanner;
			CancellationTokenSource oldCancellation;
			lock (sync)
			{
				oldScanner = scanner;
				oldCancellation = cancellation;
				scanner = null;
				cancellation = null;
				worker = null;
				running = false;
				progress = 0;
			}

			try
			{
				oldScanner?.Dispose();
			}
			catch (Exception)
			{
				// ignored
			}

			try
			{
				oldCancellation?.Dispose();
			}
			catch (Exception)
			{
				// ignored
			}
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
			throw RpcException.BadArgument($"unknown value type '{name}', expected one of {string.Join(", ", ValueTypes.Keys)}");
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
			throw RpcException.BadArgument($"unknown compare type '{name}'");
		}

		/// <summary>The compare types which need the value of the previous scan.</summary>
		private static readonly HashSet<ScanCompareType> PreviousValueCompares = new HashSet<ScanCompareType>
		{
			ScanCompareType.Changed,
			ScanCompareType.NotChanged,
			ScanCompareType.Increased,
			ScanCompareType.IncreasedOrEqual,
			ScanCompareType.Decreased,
			ScanCompareType.DecreasedOrEqual
		};

		/// <summary>
		/// Rejects the compare types the scanner cannot serve, up front, so that a
		/// bad request fails with <c>-32002</c> instead of being reported as a scan
		/// which found nothing:
		/// <list type="bullet">
		/// <item>a first scan has no previous value, so <c>changed</c>,
		/// <c>not_changed</c>, <c>increased</c>, <c>increased_or_equal</c>,
		/// <c>decreased</c> and <c>decreased_or_equal</c> need a <c>scan.next</c>;</item>
		/// <item><c>bytes</c>, <c>string</c> and <c>regex</c> comparers implement
		/// equality only.</item>
		/// </list>
		/// </summary>
		private static void ValidateCompare(ScanValueType valueType, ScanCompareType compareType, bool isFirstScan)
		{
			if (isFirstScan && PreviousValueCompares.Contains(compareType))
			{
				throw RpcException.BadArgument($"compare type '{compareType}' needs a previous scan, use scan.next");
			}

			switch (valueType)
			{
				case ScanValueType.ArrayOfBytes:
				case ScanValueType.String:
				case ScanValueType.Regex:
					if (compareType != ScanCompareType.Equal)
					{
						throw RpcException.BadArgument($"value type '{valueType}' only supports the 'equal' compare type");
					}
					break;
			}
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
					throw RpcException.BadArgument($"'{name}' must be \"yes\", \"no\" or \"indeterminate\"");
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
					throw RpcException.BadArgument("'alignment' must be at least 1");
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

			// Addresses are unsigned: on 64 bit a kernel-space address has the sign
			// bit set and would compare as "less than" a user-space one.
			if ((ulong)settings.StopAddress.ToInt64() <= (ulong)settings.StartAddress.ToInt64())
			{
				throw RpcException.BadArgument("'stop' must be greater than 'start'");
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
					// A sign in front of a hex literal is ambiguous (is 0x… already the
					// two's complement form?), so it is rejected instead of guessed.
					if (negative)
					{
						throw RpcException.BadArgument($"'{name}' must not combine a sign with a hex literal");
					}
					if (!ulong.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
					{
						throw RpcException.BadArgument($"'{name}' is not a valid integer");
					}
					return unchecked((long)hex);
				}

				if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
				{
					// Accept unsigned values which do not fit into a long, e.g. 0xFEEDFACF written as 4277009103.
					if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
					{
						return unchecked((long)unsigned);
					}
					throw RpcException.BadArgument($"'{name}' is not a valid integer");
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
				throw RpcException.BadArgument($"'{name}' is not a valid integer");
			}
		}

		private static double ParseFloat(object value, string name)
		{
			if (value is string text)
			{
				if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
				{
					throw RpcException.BadArgument($"'{name}' is not a valid number");
				}
				return parsed;
			}

			try
			{
				return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
			}
			catch (Exception)
			{
				throw RpcException.BadArgument($"'{name}' is not a valid number");
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
					throw RpcException.BadArgument("'encoding' must be utf8, utf16 or utf32");
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
						throw RpcException.BadArgument($"compare type '{compareType}' requires 'value2'");
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
						throw RpcException.BadArgument($"compare type '{compareType}' requires 'value2'");
					}

					if (needsSecondValue && value1 > value2)
					{
						var temp = value1;
						value1 = value2;
						value2 = temp;
					}

					// 'significant_digits' defaults the way ScannerForm does it: from
					// the number of decimal places of the literal the caller supplied
					// (the larger of 'value' and 'value2'). An integer literal such as
					// "42" or 42 carries no decimals, so 3 digits are used there rather
					// than an exact-equality compare. An explicit value must be 0..15,
					// the range in which a double can carry significant digits.
					var roundMode = ParseRoundMode(Params.GetOptional(p, "round", "normal"));

					var defaultDigits = Math.Max(
						CountDecimals(Params.Has(p, "value") ? p["value"] : null),
						CountDecimals(Params.Has(p, "value2") ? p["value2"] : null));
					if (defaultDigits == 0)
					{
						defaultDigits = 3;
					}

					var digits = Params.GetOptional(p, "significant_digits", defaultDigits);
					if (digits < 0 || digits > 15)
					{
						throw RpcException.BadArgument("'significant_digits' must be between 0 and 15");
					}

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
						throw RpcException.BadArgument($"'value' is not a valid byte pattern: {ex.Message}");
					}
				}

				case ScanValueType.String:
				{
					var value = Params.Get<string>(p, "value");
					if (string.IsNullOrEmpty(value))
					{
						throw RpcException.BadArgument("'value' must not be empty");
					}
					return new StringMemoryComparer(value, ParseEncoding(p), Params.GetOptional(p, "case_sensitive", true));
				}

				default:
				{
					var value = Params.Get<string>(p, "value");
					if (string.IsNullOrEmpty(value))
					{
						throw RpcException.BadArgument("'value' must not be empty");
					}
					try
					{
						return new RegexStringMemoryComparer(value, ParseEncoding(p), Params.GetOptional(p, "case_sensitive", true));
					}
					catch (ArgumentException ex)
					{
						throw RpcException.BadArgument($"'value' is not a valid regular expression: {ex.Message}");
					}
				}
			}
		}

		/// <summary>
		/// The number of decimal places of a supplied numeric literal, mirroring
		/// <c>ScannerForm.CalculateSignificantDigits</c>. Numbers which arrived as
		/// JSON numbers are rendered round-trip first, so 1.25 still counts as two.
		/// </summary>
		private static int CountDecimals(object value)
		{
			if (value == null)
			{
				return 0;
			}

			string text;
			switch (value)
			{
				case string s:
					text = s.Trim();
					break;
				case double d:
					text = d.ToString("R", CultureInfo.InvariantCulture);
					break;
				case float f:
					text = f.ToString("R", CultureInfo.InvariantCulture);
					break;
				default:
					text = System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
					break;
			}

			var index = text.IndexOf('.');
			if (index < 0 || text.IndexOf('E') >= 0 || text.IndexOf('e') >= 0)
			{
				return 0;
			}

			return text.Length - 1 - index;
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
					throw RpcException.BadArgument("'round' must be strict, normal or truncate");
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
			ValidateCompare(valueType, compareType, true);
			var settings = BuildSettings(p, valueType);
			var comparer = CreateComparer(p, valueType, compareType);

			lock (gate)
			{
				lock (sync)
				{
					if (running)
					{
						throw RpcException.Busy("a scan is already running");
					}
				}

				// Waits for a finished-but-not-yet-collected worker before the old
				// scanner is disposed under it.
				DisposeScanner();

				var created = new Scanner(Program.RemoteProcess, settings);
				lock (sync)
				{
					scanner = created;
				}

				return Start(comparer);
			}
		}

		private object Next(Dictionary<string, object> p)
		{
			MemoryApi.RequireProcess();

			lock (gate)
			{
				ScanValueType valueType;
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
					valueType = scanner.Settings.ValueType;
				}

				var compareType = ParseCompareType(Params.Get<string>(p, "compare"));
				ValidateCompare(valueType, compareType, false);
				var comparer = CreateComparer(p, valueType, compareType);

				return Start(comparer);
			}
		}

		/// <summary>
		/// Starts the search on a worker. Called with <see cref="gate"/> held and a
		/// non-null, idle <see cref="scanner"/>.
		/// </summary>
		private object Start(IScanComparer comparer)
		{
			int current;
			CancellationToken token;
			Scanner currentScanner;

			lock (sync)
			{
				cancellation?.Dispose();
				cancellation = new CancellationTokenSource();

				progress = 0;
				running = true;
				lastError = null;
				lastSuccess = null;
				current = ++job;
				token = cancellation.Token;
				currentScanner = scanner;
			}

			// Scanner.Search itself dispatches the work to a task; awaiting the
			// returned task on a worker keeps the RPC thread free and lets us
			// clear the running flag when it finishes, faults or is cancelled.
			var task = System.Threading.Tasks.Task.Run(async () =>
			{
				bool? success = null;
				string error = null;
				try
				{
					success = await currentScanner.Search(comparer, new Progress<int>(value =>
					{
						lock (sync)
						{
							progress = value;
						}
					}), token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					// a cancelled scan is not an error
				}
				catch (Exception ex)
				{
					// Without this the failure would be indistinguishable from a scan
					// which simply found nothing, so it is logged and reported by
					// scan.status as 'error'.
					error = ex.Message;
					try
					{
						Program.Logger?.Log(ex);
					}
					catch (Exception)
					{
						// ignored
					}
				}
				finally
				{
					lock (sync)
					{
						if (job == current)
						{
							running = false;
							progress = 100;
							lastError = error;
							lastSuccess = success;
						}
					}
				}
			});

			lock (sync)
			{
				if (job == current)
				{
					worker = task;
				}
			}

			return new Dictionary<string, object> { { "job", current } };
		}

		/// <summary>
		/// The scan state. <c>total</c> is null while a scan runs — the result
		/// store is rebuilt by the worker and must not be counted from here — and
		/// is read under the same lock as <c>progress</c> once it has finished.
		/// <c>error</c> carries the message of the exception the last worker died
		/// with (null when it succeeded or was cancelled) and <c>success</c> the
		/// return value of <c>Scanner.Search</c> (null while running / cancelled).
		/// </summary>
		private object Status(Dictionary<string, object> p)
		{
			lock (sync)
			{
				return new Dictionary<string, object>
				{
					{ "running", running },
					{ "progress", progress },
					{ "total", running ? null : (object)(scanner?.TotalResultCount ?? 0) },
					{ "error", lastError },
					{ "success", lastSuccess },
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
				throw RpcException.BadArgument("'offset' must not be negative");
			}
			if (limit < 0 || limit > MaxResultLimit)
			{
				throw RpcException.BadArgument($"'limit' must be between 0 and {MaxResultLimit}");
			}

			lock (sync)
			{
				if (scanner == null)
				{
					throw RpcException.NotFound("no scan is active, call scan.first first");
				}
				if (running)
				{
					// The worker rewrites the result store as it goes; enumerating it
					// here would race with it.
					throw RpcException.Busy("a scan is running");
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
					throw RpcException.BadArgument("there is no scan to undo");
				}

				scanner.UndoLastScan();

				return new Dictionary<string, object>
				{
					{ "ok", true },
					{ "total", scanner.TotalResultCount }
				};
			}
		}

		/// <summary>
		/// Cancels the running scan and waits (bounded) for the worker to stop, so
		/// that the scanner is idle when this returns. <c>was_running</c> tells the
		/// caller whether there was anything to cancel.
		/// </summary>
		private object Cancel(Dictionary<string, object> p)
		{
			lock (gate)
			{
				bool wasRunning;
				lock (sync)
				{
					wasRunning = running;
				}

				CancelAndWaitForWorker();

				return new Dictionary<string, object>
				{
					{ "ok", true },
					{ "was_running", wasRunning }
				};
			}
		}

		private object Reset(Dictionary<string, object> p)
		{
			lock (gate)
			{
				DisposeScanner();
			}

			lock (sync)
			{
				lastError = null;
				lastSuccess = null;
			}

			return Json.Ok();
		}
	}
}
