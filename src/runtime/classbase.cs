using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

using Python.Runtime.Slots;

namespace Python.Runtime
{
    /// <summary>
    /// Base class for Python types that reflect managed types / classes.
    /// Concrete subclasses include ClassObject and DelegateObject. This
    /// class provides common attributes and common machinery for doing
    /// class initialization (initialization of the class __dict__). The
    /// concrete subclasses provide slot implementations appropriate for
    /// each variety of reflected type.
    /// </summary>
    [Serializable]
    internal class ClassBase : ManagedType, IDeserializationCallback
    {
        [NonSerialized]
        internal List<string> dotNetMembers = new();
        internal Indexer? indexer;
        internal readonly Dictionary<int, MethodObject> richcompare = new();
        internal MaybeType type;

        internal ClassBase(Type tp)
        {
            if (tp is null) throw new ArgumentNullException(nameof(type));

            indexer = null;
            type = tp;
        }

        internal virtual bool CanSubclass()
        {
            return !type.Value.IsEnum;
        }

        public readonly static Dictionary<string, int> CilToPyOpMap = new Dictionary<string, int>
        {
            ["op_Equality"] = Runtime.Py_EQ,
            ["op_Inequality"] = Runtime.Py_NE,
            ["op_LessThanOrEqual"] = Runtime.Py_LE,
            ["op_GreaterThanOrEqual"] = Runtime.Py_GE,
            ["op_LessThan"] = Runtime.Py_LT,
            ["op_GreaterThan"] = Runtime.Py_GT,
        };

        /// <summary>
        /// Default implementation of [] semantics for reflected types.
        /// </summary>
        public virtual NewReference type_subscript(BorrowedReference idx)
        {
            Type[]? types = Runtime.PythonArgsToTypeArray(idx);
            if (types == null)
            {
                return Exceptions.RaiseTypeError("type(s) expected");
            }

            if (!type.Valid)
            {
                return Exceptions.RaiseTypeError(type.DeletedMessage);
            }

            Type? target = GenericUtil.GenericForType(type.Value, types.Length);

            if (target != null)
            {
                Type t;
                try
                {
                    // MakeGenericType can throw ArgumentException
                    t = target.MakeGenericType(types);
                }
                catch (ArgumentException e)
                {
                    return Exceptions.RaiseTypeError(e.Message);
                }
                var c = ClassManager.GetClass(t);
                return new NewReference(c);
            }

            return Exceptions.RaiseTypeError($"{type.Value.Namespace}.{type.Name} does not accept {types.Length} generic parameters");
        }

        /// <summary>
        /// Standard comparison implementation for instances of reflected types.
        /// </summary>
        public static NewReference tp_richcompare(BorrowedReference ob, BorrowedReference other, int op)
        {
            CLRObject co1;
            CLRObject? co2;
            BorrowedReference tp = Runtime.PyObject_TYPE(ob);
            var cls = (ClassBase)GetManagedObject(tp)!;
            // C# operator methods take precedence over IComparable.
            // We first check if there's a comparison operator by looking up the richcompare table,
            // otherwise fallback to checking if an IComparable interface is handled.
            if (cls.richcompare.TryGetValue(op, out var methodObject))
            {
                // Wrap the `other` argument of a binary comparison operator in a PyTuple.
                using var args = Runtime.PyTuple_New(1);
                Runtime.PyTuple_SetItem(args.Borrow(), 0, other);
                return methodObject.Invoke(ob, args.Borrow(), null);
            }

            switch (op)
            {
                case Runtime.Py_EQ:
                case Runtime.Py_NE:
                    BorrowedReference pytrue = Runtime.PyTrue;
                    BorrowedReference pyfalse = Runtime.PyFalse;

                    // swap true and false for NE
                    if (op != Runtime.Py_EQ)
                    {
                        pytrue = Runtime.PyFalse;
                        pyfalse = Runtime.PyTrue;
                    }

                    if (ob == other)
                    {
                        return new NewReference(pytrue);
                    }

                    co1 = (CLRObject)GetManagedObject(ob)!;
                    co2 = GetManagedObject(other) as CLRObject;
                    if (null == co2)
                    {
                        return new NewReference(pyfalse);
                    }

                    object o1 = co1.inst;
                    object o2 = co2.inst;

                    if (Equals(o1, o2))
                    {
                        return new NewReference(pytrue);
                    }

                    return new NewReference(pyfalse);
                case Runtime.Py_LT:
                case Runtime.Py_LE:
                case Runtime.Py_GT:
                case Runtime.Py_GE:
                    co1 = (CLRObject)GetManagedObject(ob)!;
                    co2 = GetManagedObject(other) as CLRObject;
                    if (co1 == null || co2 == null)
                    {
                        return Exceptions.RaiseTypeError("Cannot get managed object");
                    }
                    var co1Comp = co1.inst as IComparable;
                    if (co1Comp == null)
                    {
                        Type co1Type = co1.GetType();
                        return Exceptions.RaiseTypeError($"Cannot convert object of type {co1Type} to IComparable");
                    }
                    try
                    {
                        int cmp = co1Comp.CompareTo(co2.inst);

                        BorrowedReference pyCmp;
                        if (cmp < 0)
                        {
                            if (op == Runtime.Py_LT || op == Runtime.Py_LE)
                            {
                                pyCmp = Runtime.PyTrue;
                            }
                            else
                            {
                                pyCmp = Runtime.PyFalse;
                            }
                        }
                        else if (cmp == 0)
                        {
                            if (op == Runtime.Py_LE || op == Runtime.Py_GE)
                            {
                                pyCmp = Runtime.PyTrue;
                            }
                            else
                            {
                                pyCmp = Runtime.PyFalse;
                            }
                        }
                        else
                        {
                            if (op == Runtime.Py_GE || op == Runtime.Py_GT)
                            {
                                pyCmp = Runtime.PyTrue;
                            }
                            else
                            {
                                pyCmp = Runtime.PyFalse;
                            }
                        }
                        return new NewReference(pyCmp);
                    }
                    catch (ArgumentException e)
                    {
                        return Exceptions.RaiseTypeError(e.Message);
                    }
                default:
                    return new NewReference(Runtime.PyNotImplemented);
            }
        }

        /// <summary>
        /// Standard iteration support for instances of reflected types. This
        /// allows natural iteration over objects that either are IEnumerable
        /// or themselves support IEnumerator directly.
        /// </summary>
        static NewReference tp_iter_impl(BorrowedReference ob)
        {
            var co = GetManagedObject(ob) as CLRObject;
            if (co == null)
            {
                return Exceptions.RaiseTypeError("invalid object");
            }

            var e = co.inst as IEnumerable;
            IEnumerator? o;
            if (e != null)
            {
                o = e.GetEnumerator();
            }
            else
            {
                o = co.inst as IEnumerator;

                if (o == null)
                {
                    return Exceptions.RaiseTypeError("iteration over non-sequence");
                }
            }

            var elemType = typeof(object);
            var iterType = co.inst.GetType();
            foreach(var ifc in iterType.GetInterfaces())
            {
                if (ifc.IsGenericType)
                {
                    var genTypeDef = ifc.GetGenericTypeDefinition();
                    if (genTypeDef == typeof(IEnumerable<>) || genTypeDef == typeof(IEnumerator<>))
                    {
                        elemType = ifc.GetGenericArguments()[0];
                        break;
                    }
                }
            }

            return new Iterator(o, elemType).Alloc();
        }


        /// <summary>
        /// Standard __hash__ implementation for instances of reflected types.
        /// </summary>
        public static nint tp_hash(BorrowedReference ob)
        {
            var co = GetManagedObject(ob) as CLRObject;
            if (co == null)
            {
                Exceptions.RaiseTypeError("unhashable type");
                return 0;
            }
            return co.inst.GetHashCode();
        }


        /// <summary>
        /// Standard __str__ implementation for instances of reflected types.
        /// </summary>
        public static NewReference tp_str(BorrowedReference ob)
        {
            var co = GetManagedObject(ob) as CLRObject;
            if (co == null)
            {
                return Exceptions.RaiseTypeError("invalid object");
            }
            try
            {
                return Runtime.PyString_FromString(co.inst.ToString());
            }
            catch (Exception e)
            {
                if (e.InnerException != null)
                {
                    e = e.InnerException;
                }
                Exceptions.SetError(e);
                return default;
            }
        }

        public static NewReference tp_repr(BorrowedReference ob)
        {
            var co = GetManagedObject(ob) as CLRObject;
            if (co == null)
            {
                return Exceptions.RaiseTypeError("invalid object");
            }
            try
            {
                //if __repr__ is defined, use it
                var instType = co.inst.GetType();
                System.Reflection.MethodInfo methodInfo = instType.GetMethod("__repr__");
                if (methodInfo != null && methodInfo.IsPublic)
                {
                    var reprString = methodInfo.Invoke(co.inst, null) as string;
                    return reprString is null ? new NewReference(Runtime.PyNone) : Runtime.PyString_FromString(reprString);
                }

                //otherwise use the standard object.__repr__(inst)
                using var args = Runtime.PyTuple_New(1);
                Runtime.PyTuple_SetItem(args.Borrow(), 0, ob);
                using var reprFunc = Runtime.PyObject_GetAttr(Runtime.PyBaseObjectType, PyIdentifier.__repr__);
                return Runtime.PyObject_Call(reprFunc.Borrow(), args.Borrow(), null);
            }
            catch (Exception e)
            {
                if (e.InnerException != null)
                {
                    e = e.InnerException;
                }
                Exceptions.SetError(e);
                return default;
            }
        }


        /// <summary>
        /// Standard dealloc implementation for instances of reflected types.
        /// </summary>
        public static void tp_dealloc(NewReference lastRef)
        {
            Runtime.PyObject_GC_UnTrack(lastRef.Borrow());

            CallClear(lastRef.Borrow());

            IntPtr addr = lastRef.DangerousGetAddress();
            bool deleted = CLRObject.reflectedObjects.Remove(addr);
            Debug.Assert(deleted);

            DecrefTypeAndFree(lastRef.Steal());
        }

        public static int tp_clear(BorrowedReference ob)
        {
            TryFreeGCHandle(ob);

            int baseClearResult = BaseUnmanagedClear(ob);
            if (baseClearResult != 0)
            {
                return baseClearResult;
            }

            ClearObjectDict(ob);
            return 0;
        }

        internal static unsafe int BaseUnmanagedClear(BorrowedReference ob)
        {
            var type = Runtime.PyObject_TYPE(ob);
            var unmanagedBase = GetUnmanagedBaseType(type);
            var clearPtr = Util.ReadIntPtr(unmanagedBase, TypeOffset.tp_clear);
            if (clearPtr == IntPtr.Zero)
            {
                return 0;
            }
            var clear = (delegate* unmanaged[Cdecl]<BorrowedReference, int>)clearPtr;

            bool usesSubtypeClear = clearPtr == TypeManager.subtype_clear;
            if (usesSubtypeClear)
            {
                // workaround for https://bugs.python.org/issue45266 (subtype_clear)
                using var dict = Runtime.PyObject_GenericGetDict(ob);
                if (Runtime.PyMapping_HasKey(dict.Borrow(), PyIdentifier.__clear_reentry_guard__) != 0)
                    return 0;
                int res = Runtime.PyDict_SetItem(dict.Borrow(), PyIdentifier.__clear_reentry_guard__, Runtime.None);
                if (res != 0) return res;

                res = clear(ob);
                Runtime.PyDict_DelItem(dict.Borrow(), PyIdentifier.__clear_reentry_guard__);
                return res;
            }
            return clear(ob);
        }

        protected override void OnSave(BorrowedReference ob, InterDomainContext context)
        {
            base.OnSave(ob, context);
            context.Storage.AddValue("impl", this);
        }

        protected override void OnLoad(BorrowedReference ob, InterDomainContext? context)
        {
            base.OnLoad(ob, context);
            var gcHandle = GCHandle.Alloc(this);
            SetGCHandle(ob, gcHandle);
        }


        /// <summary>
        /// Implements __getitem__ for reflected classes and value types.
        /// </summary>
        static NewReference mp_subscript_impl(BorrowedReference ob, BorrowedReference idx)
        {
            BorrowedReference tp = Runtime.PyObject_TYPE(ob);
            var cls = (ClassBase)GetManagedObject(tp)!;

            if (cls.indexer == null || !cls.indexer.CanGet)
            {
                Exceptions.SetError(Exceptions.TypeError, "unindexable object");
                return default;
            }

            // Arg may be a tuple in the case of an indexer with multiple
            // parameters. If so, use it directly, else make a new tuple
            // with the index arg (method binders expect arg tuples).
            if (!Runtime.PyTuple_Check(idx))
            {
                using var argTuple = Runtime.PyTuple_New(1);
                Runtime.PyTuple_SetItem(argTuple.Borrow(), 0, idx);
                return cls.indexer.GetItem(ob, argTuple.Borrow());
            }
            else
            {
                return cls.indexer.GetItem(ob, idx);
            }
        }


        /// <summary>
        /// Implements __setitem__ for reflected classes and value types.
        /// </summary>
        static int mp_ass_subscript_impl(BorrowedReference ob, BorrowedReference idx, BorrowedReference v)
        {
            BorrowedReference tp = Runtime.PyObject_TYPE(ob);
            var cls = (ClassBase)GetManagedObject(tp)!;

            if (cls.indexer == null || !cls.indexer.CanSet)
            {
                Exceptions.SetError(Exceptions.TypeError, "object doesn't support item assignment");
                return -1;
            }

            // Arg may be a tuple in the case of an indexer with multiple
            // parameters. If so, use it directly, else make a new tuple
            // with the index arg (method binders expect arg tuples).
            NewReference argsTuple = default;

            if (!Runtime.PyTuple_Check(idx))
            {
                argsTuple = Runtime.PyTuple_New(1);
                Runtime.PyTuple_SetItem(argsTuple.Borrow(), 0, idx);
                idx = argsTuple.Borrow();
            }

            // Get the args passed in.
            var i = Runtime.PyTuple_Size(idx);
            using var defaultArgs = cls.indexer.GetDefaultArgs(idx);
            var numOfDefaultArgs = Runtime.PyTuple_Size(defaultArgs.Borrow());
            var temp = i + numOfDefaultArgs;
            using var real = Runtime.PyTuple_New(temp + 1);
            for (var n = 0; n < i; n++)
            {
                BorrowedReference item = Runtime.PyTuple_GetItem(idx, n);
                Runtime.PyTuple_SetItem(real.Borrow(), n, item);
            }

            argsTuple.Dispose();

            // Add Default Args if needed
            for (var n = 0; n < numOfDefaultArgs; n++)
            {
                BorrowedReference item = Runtime.PyTuple_GetItem(defaultArgs.Borrow(), n);
                Runtime.PyTuple_SetItem(real.Borrow(), n + i, item);
            }
            i = temp;

            // Add value to argument list
            Runtime.PyTuple_SetItem(real.Borrow(), i, v);

            cls.indexer.SetItem(ob, real.Borrow());

            if (Exceptions.ErrorOccurred())
            {
                return -1;
            }

            return 0;
        }

        static NewReference tp_call_impl(BorrowedReference ob, BorrowedReference args, BorrowedReference kw)
        {
            BorrowedReference tp = Runtime.PyObject_TYPE(ob);
            var self = (ClassBase)GetManagedObject(tp)!;

            if (!self.type.Valid)
            {
                return Exceptions.RaiseTypeError(self.type.DeletedMessage);
            }

            Type type = self.type.Value;

            var calls = GetCallImplementations(type).ToList();
            Debug.Assert(calls.Count > 0);
            var callBinder = new MethodBinder();
            foreach (MethodInfo call in calls)
            {
                callBinder.AddMethod(call);
            }
            return callBinder.Invoke(ob, args, kw);
        }

        static IEnumerable<MethodInfo> GetCallImplementations(Type type)
            => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "__call__");

        public virtual void InitializeSlots(BorrowedReference pyType, SlotsHolder slotsHolder)
        {
            if (!this.type.Valid) return;

            if (GetCallImplementations(this.type.Value).Any())
            {
                TypeManager.InitializeSlotIfEmpty(pyType, TypeOffset.tp_call, new Interop.BBB_N(tp_call_impl), slotsHolder);
            }

            if (indexer is not null)
            {
                if (indexer.CanGet)
                {
                    TypeManager.InitializeSlotIfEmpty(pyType, TypeOffset.mp_subscript, new Interop.BB_N(mp_subscript_impl), slotsHolder);
                }
                if (indexer.CanSet)
                {
                    TypeManager.InitializeSlotIfEmpty(pyType, TypeOffset.mp_ass_subscript, new Interop.BBB_I32(mp_ass_subscript_impl), slotsHolder);
                }
            }

            if (typeof(IEnumerable).IsAssignableFrom(type.Value)
                || typeof(IEnumerator).IsAssignableFrom(type.Value))
            {
                TypeManager.InitializeSlotIfEmpty(pyType, TypeOffset.tp_iter, new Interop.B_N(tp_iter_impl), slotsHolder);
            }

            if (mp_length_slot.CanAssign(type.Value))
            {
                TypeManager.InitializeSlotIfEmpty(pyType, TypeOffset.mp_length, new Interop.B_P(mp_length_slot.impl), slotsHolder);
            }
        }

        protected virtual void OnDeserialization(object sender)
        {
            this.dotNetMembers = new List<string>();
        }

        void IDeserializationCallback.OnDeserialization(object sender) => this.OnDeserialization(sender);
    }
}
