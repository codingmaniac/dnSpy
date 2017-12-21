﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.Engine;
using dnSpy.Contracts.Metadata;
using dnSpy.Debugger.DotNet.Metadata;
using dnSpy.Debugger.DotNet.Mono.Impl.Evaluation;
using dnSpy.Debugger.DotNet.Mono.Properties;
using Mono.Debugger.Soft;

namespace dnSpy.Debugger.DotNet.Mono.Impl {
	sealed partial class DbgEngineImpl {
		sealed class BoundBreakpointData : IDisposable {
			public BreakpointEventRequest Breakpoint { get; set; }
			public ModuleId Module { get; }
			public DbgEngineBoundCodeBreakpoint EngineBoundCodeBreakpoint { get; set; }
			public DbgEngineImpl Engine { get; }
			public BoundBreakpointData(DbgEngineImpl engine, in ModuleId module, BreakpointEventRequest breakpoint) {
				Engine = engine ?? throw new ArgumentNullException(nameof(engine));
				Module = module;
				Breakpoint = breakpoint;
			}
			public void Dispose() => Engine.RemoveBreakpoint(this);
		}

		void SendCodeBreakpointHitMessage_MonoDebug(BreakpointEventRequest breakpoint, DbgThread thread) {
			debuggerThread.VerifyAccess();
			var bpData = (BoundBreakpointData)breakpoint.Tag;
			Debug.Assert(bpData != null);
			if (bpData != null)
				SendMessage(new DbgMessageBreakpoint(bpData.EngineBoundCodeBreakpoint.BoundCodeBreakpoint, thread, GetMessageFlags()));
			else
				SendMessage(new DbgMessageBreak(thread, GetMessageFlags()));
		}

		void RemoveBreakpoint(BoundBreakpointData bpData) {
			if (bpData.Breakpoint == null)
				return;
			lock (lockObj) {
				pendingBreakpointsToRemove.Add(bpData.Breakpoint);
				if (pendingBreakpointsToRemove.Count == 1)
					MonoDebugThread(() => RemoveBreakpoints_MonoDebug());
			}
		}
		readonly List<BreakpointEventRequest> pendingBreakpointsToRemove = new List<BreakpointEventRequest>();

		void RemoveBreakpoints_MonoDebug() {
			debuggerThread.VerifyAccess();
			BreakpointEventRequest[] breakpointsToRemove;
			lock (lockObj) {
				breakpointsToRemove = pendingBreakpointsToRemove.Count == 0 ? Array.Empty<BreakpointEventRequest>() : pendingBreakpointsToRemove.ToArray();
				pendingBreakpointsToRemove.Clear();
			}
			if (breakpointsToRemove.Length == 0)
				return;
			try {
				using (TempBreak()) {
					foreach (var bp in breakpointsToRemove)
						bp.Disable();
				}
			}
			catch (VMDisconnectedException) {
			}
			catch (Exception ex) {
				Debug.Fail(ex.Message);
				dbgManager.ShowError(ex.Message);
			}
		}

		static Dictionary<ModuleId, List<DbgDotNetCodeLocation>> CreateDotNetCodeLocationDictionary(DbgCodeLocation[] locations) {
			var dict = new Dictionary<ModuleId, List<DbgDotNetCodeLocation>>();
			foreach (var location in locations) {
				// The BP could've gotten closed. It's more likely to happen if the debugged process is
				// completely paused when it loses keyboard focus. Mono.Debugger.Soft will block until
				// it gets a reply back from the now paused process.
				if (location.IsClosed)
					continue;
				if (location is DbgDotNetCodeLocation loc) {
					if (!dict.TryGetValue(loc.Module, out var list))
						dict.Add(loc.Module, list = new List<DbgDotNetCodeLocation>());
					list.Add(loc);
				}
			}
			return dict;
		}

		public override void AddBreakpoints(DbgModule[] modules, DbgCodeLocation[] locations, bool includeNonModuleBreakpoints) =>
			MonoDebugThread(() => AddBreakpointsCore(modules, locations, includeNonModuleBreakpoints));
		void AddBreakpointsCore(DbgModule[] modules, DbgCodeLocation[] locations, bool includeNonModuleBreakpoints) {
			debuggerThread.VerifyAccess();

			try {
				var dict = CreateDotNetCodeLocationDictionary(locations);
				if (dict.Count == 0)
					return;

				foreach (var module in modules) {
					if (!TryGetModuleData(module, out var data))
						continue;
					if (dict.TryGetValue(data.ModuleId, out var moduleLocations))
						EnableBreakpoints(data.MonoModule, module, moduleLocations);
				}
			}
			catch (Exception ex) {
				Debug.Fail(ex.Message);
				dbgManager.ShowError(ex.Message);
			}
		}

		void EnableBreakpoints(ModuleMirror monoModule, DbgModule module, List<DbgDotNetCodeLocation> moduleLocations) {
			debuggerThread.VerifyAccess();
			if (moduleLocations.Count == 0)
				return;

			var createdBreakpoints = new DbgBoundCodeBreakpointInfo<BoundBreakpointData>[moduleLocations.Count];
			var reflectionModule = module.GetReflectionModule();
			var state = module.GetOrCreateData<TypeLoadBreakpointState>();
			for (int i = 0; i < createdBreakpoints.Length; i++) {
				var location = moduleLocations[i];
				const ulong address = DbgObjectFactory.BoundBreakpointNoAddress;

				DbgEngineBoundCodeBreakpointMessage msg;
				var method = reflectionModule.ResolveMethod((int)location.Token, DmdResolveOptions.None);
				if ((object)method == null)
					msg = DbgEngineBoundCodeBreakpointMessage.CreateFunctionNotFound(GetFunctionName(location));
				else {
					var warning = state.IsTypeLoaded(method.DeclaringType.MetadataToken) ?
						dnSpy_Debugger_DotNet_Mono_Resources.CanNotSetABreakpointWhenProcessIsPaused :
						dnSpy_Debugger_DotNet_Mono_Resources.TypeHasNotBeenLoadedYet;
					msg = DbgEngineBoundCodeBreakpointMessage.CreateCustomWarning(warning);
				}
				var bpData = new BoundBreakpointData(this, location.Module, null);
				createdBreakpoints[i] = new DbgBoundCodeBreakpointInfo<BoundBreakpointData>(location, module, address, msg, bpData);
			}

			var boundBreakpoints = objectFactory.Create(createdBreakpoints.ToArray());
			foreach (var ebp in boundBreakpoints) {
				if (!ebp.BoundCodeBreakpoint.TryGetData(out BoundBreakpointData bpData)) {
					Debug.Assert(ebp.BoundCodeBreakpoint.IsClosed);
					continue;
				}
				bpData.EngineBoundCodeBreakpoint = ebp;
				if (bpData.Breakpoint != null)
					bpData.Breakpoint.Tag = bpData;
			}

			for (int i = 0; i < boundBreakpoints.Length; i++) {
				var boundBp = boundBreakpoints[i];
				var location = (DbgDotNetCodeLocation)boundBp.BoundCodeBreakpoint.Breakpoint.Location;
				var method = reflectionModule.ResolveMethod((int)location.Token, DmdResolveOptions.None);
				if ((object)method == null)
					continue;

				state.AddBreakpoint(method.DeclaringType.MetadataToken, boundBp, () => EnableBreakpointCore(module, method, boundBp, location));
			}
		}

		void EnableBreakpointCore(DbgModule module, DmdMethodBase method, DbgEngineBoundCodeBreakpoint ebp, DbgDotNetCodeLocation location) {
			debuggerThread.VerifyAccess();
			if (ebp.BoundCodeBreakpoint.IsClosed)
				return;
			using (TempBreak()) {
				var info = CreateBreakpoint(method.Module, location);
				if (!ebp.BoundCodeBreakpoint.TryGetData(out BoundBreakpointData bpData)) {
					Debug.Assert(ebp.BoundCodeBreakpoint.IsClosed);
					return;
				}
				Debug.Assert(bpData.Breakpoint == null);
				bpData.Breakpoint = info.bp;
				if (bpData.Breakpoint != null)
					bpData.Breakpoint.Tag = bpData;
				ebp.UpdateMessage(info.error);
			}
		}

		sealed class TypeLoadBreakpointState {
			readonly HashSet<int> loadedTypes = new HashSet<int>();
			readonly Dictionary<int, List<PendingBreakpoint>> pendingBreakpoints = new Dictionary<int, List<PendingBreakpoint>>();

			readonly struct PendingBreakpoint {
				public DbgEngineBoundCodeBreakpoint BoundBreakpoint { get; }
				public Action OnTypeLoaded { get; }
				public PendingBreakpoint(DbgEngineBoundCodeBreakpoint boundBreakpoint, Action onTypeLoaded) {
					BoundBreakpoint = boundBreakpoint;
					OnTypeLoaded = onTypeLoaded;
				}
			}

			public bool IsTypeLoaded(int metadataToken) => (metadataToken & 0x00FFFFFF) != 0 && loadedTypes.Contains(metadataToken);

			public void OnTypeLoaded(TypeMirror monoType) {
				int typeToken = monoType.MetadataToken;
				if ((typeToken & 0x00FFFFFF) == 0)
					return;

				// This can fail if it's a generic instantiated type, eg. List<int> if List<string> has already been loaded
				bool b = loadedTypes.Add(typeToken);
				if (!b)
					return;

				if (pendingBreakpoints.TryGetValue(typeToken, out var list)) {
					pendingBreakpoints.Remove(typeToken);
					foreach (var info in list)
						NotifyLoaded(info);
				}
			}

			public void AddBreakpoint(int typeToken, DbgEngineBoundCodeBreakpoint boundBreakpoint, Action onTypeLoaded) {
				var pendingBreakpoint = new PendingBreakpoint(boundBreakpoint, onTypeLoaded);
				if (loadedTypes.Contains(typeToken))
					NotifyLoaded(pendingBreakpoint);
				else {
					if (!pendingBreakpoints.TryGetValue(typeToken, out var list))
						pendingBreakpoints.Add(typeToken, list = new List<PendingBreakpoint>());
					list.Add(pendingBreakpoint);
				}
			}

			void NotifyLoaded(in PendingBreakpoint pendingBreakpoint) {
				if (!pendingBreakpoint.BoundBreakpoint.BoundCodeBreakpoint.IsClosed)
					pendingBreakpoint.OnTypeLoaded();
			}
		}

		void InitializeBreakpoints(TypeMirror monoType) {
			debuggerThread.VerifyAccess();
			Debug.Assert(monoType != null);
			if (monoType == null)
				return;
			var module = TryGetModuleCore_NoCreate(monoType.Module);
			if (module == null)
				return;

			var state = module.GetOrCreateData<TypeLoadBreakpointState>();
			state.OnTypeLoaded(monoType);
		}

		(BreakpointEventRequest bp, DbgEngineBoundCodeBreakpointMessage error) CreateBreakpoint(DmdModule module, DbgDotNetCodeLocation location) {
			DmdMethodBase method;
			MethodMirror monoMethod;
			try {
				method = module.ResolveMethod((int)location.Token);
				if ((object)method == null)
					return (null, DbgEngineBoundCodeBreakpointMessage.CreateFunctionNotFound(GetFunctionName(location)));
				monoMethod = MethodCache.GetMethod(method, null);
			}
			catch (Exception ex) when (ExceptionUtils.IsInternalDebuggerError(ex)) {
				return (null, DbgEngineBoundCodeBreakpointMessage.CreateFunctionNotFound(GetFunctionName(location)));
			}

			try {
				var bp = vm.CreateBreakpointRequest(monoMethod, location.Offset);
				bp.Enable();
				return (bp, DbgEngineBoundCodeBreakpointMessage.CreateNoError());
			}
			catch (Exception ex) when (ExceptionUtils.IsInternalDebuggerError(ex)) {
				// ArgumentException is thrown if we get error NO_SEQ_POINT_AT_IL_OFFSET
				return (null, DbgEngineBoundCodeBreakpointMessage.CreateCouldNotCreateBreakpoint());
			}
		}

		static string GetFunctionName(DbgDotNetCodeLocation location) => $"0x{location.Token:X8} ({location.Module.ModuleName})";

		Dictionary<ModuleId, List<BoundBreakpointData>> CreateBoundBreakpointsDictionary(DbgBoundCodeBreakpoint[] boundBreakpoints) {
			var dict = new Dictionary<ModuleId, List<BoundBreakpointData>>();
			foreach (var bound in boundBreakpoints) {
				if (!bound.TryGetData(out BoundBreakpointData bpData) || bpData.Engine != this)
					continue;
				if (!dict.TryGetValue(bpData.Module, out var list))
					dict.Add(bpData.Module, list = new List<BoundBreakpointData>());
				list.Add(bpData);
			}
			return dict;
		}

		public override void RemoveBreakpoints(DbgModule[] modules, DbgBoundCodeBreakpoint[] boundBreakpoints, bool includeNonModuleBreakpoints) {
			var dict = CreateBoundBreakpointsDictionary(boundBreakpoints);
			var bpsToRemove = new List<BoundBreakpointData>();
			foreach (var module in modules) {
				if (!TryGetModuleData(module, out var data))
					continue;
				if (!dict.TryGetValue(data.ModuleId, out var bpDataList))
					continue;
				bpsToRemove.AddRange(bpDataList);
			}
			if (bpsToRemove.Count > 0)
				bpsToRemove[0].EngineBoundCodeBreakpoint.Remove(bpsToRemove.Select(a => a.EngineBoundCodeBreakpoint).ToArray());
		}
	}
}
