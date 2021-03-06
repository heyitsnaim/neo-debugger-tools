﻿using Neo.VM;
using Neo.Cryptography;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Neo.Emulator;
using System;
using System.Numerics;

namespace Neo.Debugger
{
    public struct DebuggerState
    {
        public enum State
        {
            Invalid,
            Reset,
            Running,
            Finished,
            Exception,
            Break
        }

        public readonly State state;
        public readonly int offset;

        public DebuggerState(State state, int offset)
        {
            this.state = state;
            this.offset = offset;
        }
    }

    public class NeoDebugger
    {
        private ExecutionEngine engine;
        private byte[] contractBytes;
        private InteropService interop;

        private HashSet<int> _breakpoints = new HashSet<int>();
        public IEnumerable<int> Breakpoints { get { return _breakpoints; } }

        private DebuggerState lastState = new DebuggerState(DebuggerState.State.Invalid, -1);

        public NeoDebugger(byte[] contractBytes)
        {
            this.interop = new InteropService();
            this.contractBytes = contractBytes;

            var assembly = typeof(Neo.Emulator.Helper).Assembly;
            var methods = assembly.GetTypes()
                                  .SelectMany(t => t.GetMethods())
                                  .Where(m => m.GetCustomAttributes(typeof(SyscallAttribute), false).Length > 0)
                                  .ToArray();

            foreach (var method in methods)
            {
                var attr = (SyscallAttribute) method.GetCustomAttributes(typeof(SyscallAttribute), false).FirstOrDefault();
                interop.Register(attr.Method, (engine) => { return (bool) method.Invoke(null, new object[] { engine }); });
            }
        }

        private int lastOffset = -1;

        public List<object> ContractArgs = new List<object>();

        public void Reset()
        {
            if (lastState.state == DebuggerState.State.Reset)
            {
                return;
            }

            engine = new ExecutionEngine(null, Crypto.Default, null, interop);
            engine.LoadScript(contractBytes);

            using (ScriptBuilder sb = new ScriptBuilder())
            {
                var items = new Stack<object>();
                foreach (var item in ContractArgs)
                {
                    items.Push(item);
                }

                while (items.Count> 0)
                {
                    var item = items.Pop();

                    if (item == null)
                    {
                        sb.EmitPush("");
                    }
                    else
                    if (item is string)
                    {
                        sb.EmitPush((string)item);
                    }
                    else
                    if (item is int)
                    {
                        BigInteger num = (int)item;
                        sb.EmitPush(num);
                    }
                    else
                    {
                        throw new Exception("Unsupport contract param: " + item.ToString());
                    }
                }

                engine.LoadScript(sb.ToArray());
            }

            foreach (var pos in _breakpoints)
            {
                engine.AddBreakPoint((uint)pos);
            }

            engine.Reset();

            lastState = new DebuggerState(DebuggerState.State.Reset, 0);
        }

        public void SetBreakpointState(int ofs, bool enabled)
        {
            if (enabled)
            {
                _breakpoints.Add(ofs);
            }
            else
            {
                _breakpoints.Remove(ofs);
            }

            try
            {
                if (enabled)
                {
                    engine.AddBreakPoint((uint)ofs);
                }
                else
                {
                    engine.RemoveBreakPoint((uint)ofs);
                }
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// executes a single instruction in the current script, and returns the last script offset
        /// </summary>
        public DebuggerState Step()
        {
            if (lastState.state == DebuggerState.State.Finished || lastState.state == DebuggerState.State.Invalid)
            {
                return lastState;
            }

            engine.ExecuteSingleStep();

            try
            {
                lastOffset = engine.CurrentContext.InstructionPointer;
            }
            catch
            {
                // failed to get instruction pointer
            }

            if (engine.State.HasFlag(VMState.FAULT))
            {
                lastState = new DebuggerState(DebuggerState.State.Exception, lastOffset);
                return lastState;
            }

            if (engine.State.HasFlag(VMState.BREAK))
            {
                lastState = new DebuggerState(DebuggerState.State.Break, lastOffset);
                return lastState;
            }

            if (engine.State.HasFlag(VMState.HALT))
            {
                lastState = new DebuggerState(DebuggerState.State.Finished, lastOffset);
                return lastState;
            }

            lastState = new DebuggerState(DebuggerState.State.Running, lastOffset);
            return lastState;
        }

        /// <summary>
        /// executes the script until it finishes, fails or hits a breakpoint
        /// </summary>
        public DebuggerState Run()
        {
            do
            {
                lastState = Step();
            } while (lastState.state == DebuggerState.State.Running);

            return lastState;
        }

        public object GetResult()
        {
            var result = engine.EvaluationStack.Peek();
            return result.GetString();
        }

    }
}
