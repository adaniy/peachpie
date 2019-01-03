﻿using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using Pchp.CodeAnalysis.FlowAnalysis;
using System.Diagnostics;
using Pchp.CodeAnalysis.Symbols;
using Pchp.CodeAnalysis.CodeGen;
using Microsoft.CodeAnalysis.CodeGen;
using Devsense.PHP.Syntax;
using Ast = Devsense.PHP.Syntax.Ast;
using Peachpie.CodeAnalysis.Utilities;

namespace Pchp.CodeAnalysis.Semantics
{
    #region BoundAccess

    /// <summary>
    /// Expression access information.
    /// Describes the context in which an expression is used.
    /// </summary>
    [DebuggerDisplay("[{_flags}]")]
    public struct BoundAccess
    {
        /// <summary>
        /// The expression access kind - read, write, ensured.
        /// </summary>
        AccessMask _flags;

        /// <summary>
        /// Optional. Type the expression will be converted to.
        /// </summary>
        TypeSymbol _targetType;

        /// <summary>
        /// Type information for the write access (right value of the assignment).
        /// In case of <see cref="EnsureArray"/>, the type represents the written element type.
        /// </summary>
        TypeRefMask _writeTypeMask;

        #region Properties

        /// <summary>
        /// In case the expression's value will be read.
        /// </summary>
        public bool IsRead => (_flags & AccessMask.Read) != 0;

        /// <summary>
        /// In case a value will be written to the variable.
        /// </summary>
        public bool IsWrite => (_flags & AccessMask.Write) != 0;

        /// <summary>
        /// In case a variable will be unset.
        /// </summary>
        public bool IsUnset => (_flags & AccessMask.Unset) != 0;

        /// <summary>
        /// In case the expression is read within <c>isset</c> operation.
        /// </summary>
        public bool IsIsSet => (_flags & AccessMask.Isset) == AccessMask.Isset;

        /// <summary>
        /// Gets type of value to be written.
        /// </summary>
        public TypeRefMask WriteMask => _writeTypeMask;

        /// <summary>
        /// Optional. Type the expression will be converted to.
        /// </summary>
        internal TypeSymbol TargetType => _targetType;

        /// <summary>
        /// Gets inyternal access flags.
        /// </summary>
        public AccessMask Flags => _flags;

        /// <summary>
        /// The variable will be aliased and read.
        /// </summary>
        public bool IsReadRef => (_flags & AccessMask.ReadRef) == AccessMask.ReadRef;

        /// <summary>
        /// A reference will be written.
        /// </summary>
        public bool IsWriteRef => (_flags & AccessMask.WriteRef) == AccessMask.WriteRef;

        /// <summary>
        /// The expression won't be read or written to.
        /// </summary>
        public bool IsNone => (_flags == 0);

        /// <summary>
        /// The read is for check purposes only and won't result in a warning in case the variable does not exist.
        /// </summary>
        public bool IsQuiet => (_flags & AccessMask.ReadQuiet) != 0;

        /// <summary>
        /// In case we might change the variable content to array, object or an alias (we may need write access).
        /// </summary>
        public bool IsEnsure => (_flags & ~AccessMask.Read & (AccessMask.ReadRef | AccessMask.EnsureObject | AccessMask.EnsureArray)) != 0;

        /// <summary>
        /// Gets value indicating the variable might be changed in context of the access.
        /// </summary>
        public bool MightChange => IsWrite || IsUnset || IsEnsure;

        /// <summary>
        /// In case an alias will be written to the variable.
        /// </summary>
        public bool WriteRef => (_flags & AccessMask.WriteRef) == AccessMask.WriteRef;

        /// <summary>
        /// In case the expression has to read as an object to allow writing its fields.
        /// In case of a variable, created object has to be written back.
        /// </summary>
        public bool EnsureObject => (_flags & AccessMask.EnsureObject) == AccessMask.EnsureObject;

        /// <summary>
        /// In case variable will be accessed as array in manner of setting its entries.
        /// <code>VARIABLE[] = ...</code>
        /// </summary>
        public bool EnsureArray => (_flags & AccessMask.EnsureArray) == AccessMask.EnsureArray;

        #endregion

        #region Construction

        private BoundAccess(AccessMask flags, TypeSymbol targetType, TypeRefMask writeTypeMask)
        {
            _flags = flags;
            _writeTypeMask = writeTypeMask;
            _targetType = targetType;

            Debug.Assert(EnsureArray ^ EnsureObject ^ IsReadRef || !IsEnsure);  // only single ensure is possible
        }

        public BoundAccess WithRead()
        {
            return new BoundAccess(_flags | AccessMask.Read, _targetType, _writeTypeMask);
        }

        public BoundAccess WithWrite(TypeRefMask writeTypeMask)
        {
            return new BoundAccess(_flags | AccessMask.Write, _targetType, _writeTypeMask | writeTypeMask);
        }

        public BoundAccess WithWriteRef(TypeRefMask writeTypeMask)
        {
            return new BoundAccess(_flags | AccessMask.WriteRef, _targetType, _writeTypeMask | writeTypeMask);
        }

        public BoundAccess WithReadRef()
        {
            return new BoundAccess(_flags | AccessMask.ReadRef, _targetType, _writeTypeMask);
        }

        internal BoundAccess WithRead(TypeSymbol target)
        {
            Contract.ThrowIfNull(target);
            return new BoundAccess(_flags | AccessMask.Read, target, _writeTypeMask);
        }

        public BoundAccess WithQuiet()
        {
            return new BoundAccess(_flags | AccessMask.ReadQuiet, _targetType, _writeTypeMask);
        }

        public BoundAccess WithEnsureObject()
        {
            return new BoundAccess(_flags | AccessMask.EnsureObject, _targetType, _writeTypeMask);
        }

        public BoundAccess WithEnsureArray()
        {
            return new BoundAccess(_flags | AccessMask.EnsureArray, _targetType, _writeTypeMask);
        }

        /// <summary>
        /// Simple read access.
        /// </summary>
        public static BoundAccess Read => new BoundAccess(AccessMask.Read, null, 0);

        /// <summary>
        /// Read as a reference access.
        /// </summary>
        public static BoundAccess ReadRef => new BoundAccess(AccessMask.ReadRef, null, 0);

        /// <summary>
        /// Simple write access without bound write type mask.
        /// </summary>
        public static BoundAccess Write => new BoundAccess(AccessMask.Write, null, 0);

        /// <summary>
        /// Unset variable.
        /// </summary>
        public static BoundAccess Unset => new BoundAccess(AccessMask.Unset | AccessMask.ReadQuiet, null, 0);

        /// <summary>
        /// Check for isset.
        /// </summary>
        public static BoundAccess Isset => new BoundAccess(AccessMask.Isset, null, 0);

        /// <summary>
        /// Expression won't be read or written to.
        /// </summary>
        public static BoundAccess None => new BoundAccess(AccessMask.None, null, 0);

        /// <summary>
        /// Read and write without bound write type mask
        /// </summary>
        public static BoundAccess ReadAndWrite => new BoundAccess(AccessMask.Read | AccessMask.Write, null, 0);

        #endregion
    }

    #endregion

    #region BoundExpression

    public abstract partial class BoundExpression : BoundOperation, IPhpExpression
    {
        /// <summary>
        /// The type analysis result.
        /// Provides possible combination of types that this expression might result in.
        /// </summary>
        public TypeRefMask TypeRefMask { get; set; } = default(TypeRefMask);

        /// <summary>
        /// Additional expression access,
        /// specifies how the expression is being accessed.
        /// </summary>
        public BoundAccess Access { get; internal set; }

        /// <summary>
        /// Lazily resolved type of the expression,
        /// after applying the <see cref="Access"/>.
        /// </summary>
        internal TypeSymbol ResultType { get; set; }

        public Ast.LangElement PhpSyntax { get; set; }

        /// <summary>
        /// Lazily resolved type of the expression result.
        /// </summary>
        public sealed override ITypeSymbol Type => ResultType;

        /// <summary>
        /// Whether the expression needs current <c>Pchp.Core.Context</c> to be evaluated.
        /// Otherwise, the expression can be evaluated in app context or in compile time.
        /// </summary>
        /// <remarks>
        /// E.g. If the expression is a literal, a resolved constant or immutable, it does not require the Context.
        /// </remarks>
        public virtual bool RequiresContext => !this.ConstantValue.HasValue;

        /// <summary>
        /// Resolved value of the expression.
        /// </summary>
        public Optional<object> ConstantValue { get; set; }

        protected sealed override Optional<object> ConstantValueHlp => ConstantValue;

        public abstract TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor);
    }

    #endregion

    #region BoundFunctionCall, BoundArgument, BoundEcho, BoundConcatEx, BoundNewEx

    public partial class BoundArgument : BoundOperation, IArgumentOperation, IPhpOperation
    {
        public ArgumentKind ArgumentKind { get; private set; }

        public CommonConversion InConversion => default(CommonConversion);

        /// <summary>
        /// Variable unpacking in PHP, the triple-dot syntax.
        /// </summary>
        public bool IsUnpacking => this.ArgumentKind == ArgumentKind.ParamArray;

        public override OperationKind Kind => OperationKind.Argument;

        public CommonConversion OutConversion => default(CommonConversion);

        public IParameterSymbol Parameter { get; set; }

        public SyntaxNode Syntax => null;

        public Ast.LangElement PhpSyntax { get; set; }

        IOperation IArgumentOperation.Value => Value;

        public BoundExpression Value { get; set; }

        public override ITypeSymbol Type => Value.ResultType;

        protected override Optional<object> ConstantValueHlp => Value.ConstantValue;

        /// <summary>
        /// Creates the argument.
        /// </summary>
        public static BoundArgument Create(BoundExpression value)
        {
            return new BoundArgument(value, ArgumentKind.Explicit);
        }

        /// <summary>
        /// Creates the argument that will be unpacked.
        /// The argument is an array which elements will be passed as actual arguments.
        /// </summary>
        public static BoundArgument CreateUnpacking(BoundExpression value)
        {
            Debug.Assert(!value.Access.IsReadRef);
            return new BoundArgument(value, ArgumentKind.ParamArray);
        }

        private BoundArgument(BoundExpression value, ArgumentKind kind = ArgumentKind.Explicit)
        {
            Contract.ThrowIfNull(value);
            Debug.Assert(value.Access.IsRead);  // we do not support OUT parameters in PHP I guess, just aliasing ~ IsReadRef

            this.Value = value;
            this.ArgumentKind = kind;
        }

        public BoundArgument Update(BoundExpression value, ArgumentKind kind)
        {
            if (value == Value && kind == ArgumentKind)
            {
                return this;
            }
            else
            {
                return new BoundArgument(value, kind);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitArgument(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitArgument(this, argument);

        public TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitArgument(this);

        TResult IPhpOperation.Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitArgument(this);
    }

    /// <summary>
    /// Represents a function call.
    /// </summary>
    public abstract partial class BoundRoutineCall : BoundExpression, IInvocationOperation
    {
        protected ImmutableArray<BoundArgument> _arguments;
        protected ImmutableArray<IBoundTypeRef> _typeargs;

        ImmutableArray<IArgumentOperation> IInvocationOperation.Arguments => StaticCast<IArgumentOperation>.From(_arguments);

        public ImmutableArray<BoundArgument> ArgumentsInSourceOrder { get => _arguments; internal set => _arguments = value; }

        public ImmutableArray<IBoundTypeRef> TypeArguments { get => _typeargs; internal set => _typeargs = value; }

        public IArgumentOperation ArgumentMatchingParameter(IParameterSymbol parameter)
        {
            foreach (var arg in _arguments)
            {
                if (arg.Parameter == parameter)
                    return arg;
            }

            return null;
        }

        IOperation IInvocationOperation.Instance => Instance;

        IMethodSymbol IInvocationOperation.TargetMethod => TargetMethod;

        /// <summary>
        /// <c>this</c> argument to be supplied to the method.
        /// </summary>
        public abstract BoundExpression Instance { get; }

        /// <summary>
        /// Resolved method if possible.
        /// </summary>
        internal MethodSymbol TargetMethod { get; set; }

        public virtual bool IsVirtual => false;

        public override OperationKind Kind => OperationKind.Invocation;

        /// <summary>
        /// Gets value indicating the arguments has to be unpacked in runtime before passed to the function.
        /// </summary>
        public bool HasArgumentsUnpacking
        {
            get
            {
                // the last argument must be unpacking,
                // otherwise unpacking is not even allowed
                var args = _arguments;
                return args.Length != 0 && args.Last().IsUnpacking;
            }
        }

        public BoundRoutineCall(ImmutableArray<BoundArgument> arguments)
        {
            Debug.Assert(!arguments.IsDefault);
            _arguments = arguments;
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitInvocation(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitInvocation(this, argument);
    }

    /// <summary>
    /// Direct or indirect routine name.
    /// </summary>
    [DebuggerDisplay("{DebugView,nq}")]
    public partial class BoundRoutineName : BoundOperation, IPhpOperation
    {
        public QualifiedName NameValue => _nameValue;
        readonly QualifiedName _nameValue;

        public BoundExpression NameExpression => _nameExpression;
        readonly BoundExpression _nameExpression;

        string DebugView
        {
            get
            {
                return IsDirect ? _nameValue.ToString() : _nameExpression.ToString();
            }
        }

        public bool IsDirect => _nameExpression == null;

        public override OperationKind Kind => OperationKind.None;

        public Ast.LangElement PhpSyntax { get; set; }

        public BoundRoutineName(QualifiedName name)
            : this(name, null)
        {
        }

        public BoundRoutineName(BoundExpression nameExpr)
            : this(default, nameExpr)
        {
        }

        private BoundRoutineName(QualifiedName name, BoundExpression nameExpr)
        {
            Debug.Assert(name.IsEmpty() != (nameExpr == null));
            _nameValue = name;
            _nameExpression = nameExpr;
        }

        public BoundRoutineName Update(QualifiedName name, BoundExpression nameExpr)
        {
            if (name.NameEquals(_nameValue) && nameExpr == _nameExpression)
            {
                return this;
            }
            else
            {
                return new BoundRoutineName(name, nameExpr);
            }
        }

        public TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor)
            => visitor.VisitRoutineName(this);

        public override void Accept(OperationVisitor visitor)
            => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.DefaultVisit(this, argument);
    }

    public partial class BoundGlobalFunctionCall : BoundRoutineCall
    {
        public override BoundExpression Instance => null;

        public BoundRoutineName Name => _name;
        readonly BoundRoutineName _name;

        public QualifiedName? NameOpt => _nameOpt;
        readonly QualifiedName? _nameOpt;

        public BoundGlobalFunctionCall(BoundExpression nameExpression, ImmutableArray<BoundArgument> arguments)
            : this(new BoundRoutineName(nameExpression), null, arguments)
        {
        }

        public BoundGlobalFunctionCall(QualifiedName name, QualifiedName? nameOpt, ImmutableArray<BoundArgument> arguments)
            : this(new BoundRoutineName(name), nameOpt, arguments)
        {
        }

        private BoundGlobalFunctionCall(BoundRoutineName name, QualifiedName? nameOpt, ImmutableArray<BoundArgument> arguments)
            : base(arguments)
        {
            Debug.Assert(nameOpt == null || name.IsDirect);
            _name = name;
            _nameOpt = nameOpt;
        }

        public BoundGlobalFunctionCall Update(BoundRoutineName name, QualifiedName? nameOpt, ImmutableArray<BoundArgument> arguments, ImmutableArray<IBoundTypeRef> typeArguments = default)
        {
            if (name == _name && nameOpt == _nameOpt && arguments == ArgumentsInSourceOrder && typeArguments == _typeargs)
            {
                return this;
            }
            else
            {
                return new BoundGlobalFunctionCall(name, nameOpt, arguments) { TypeArguments = typeArguments }.WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitGlobalFunctionCall(this);
    }

    public partial class BoundInstanceFunctionCall : BoundRoutineCall
    {
        public override BoundExpression Instance => _instance;
        private BoundExpression _instance;

        public BoundRoutineName Name => _name;
        readonly BoundRoutineName _name;

        public override bool IsVirtual => this.TargetMethod.IsErrorMethodOrNull() || this.TargetMethod.IsVirtual;

        internal void SetInstance(BoundExpression instance) => _instance = instance;

        public BoundInstanceFunctionCall(BoundExpression instance, QualifiedName name, ImmutableArray<BoundArgument> arguments)
            : this(instance, new BoundRoutineName(name), arguments)
        {

        }

        public BoundInstanceFunctionCall(BoundExpression instance, BoundExpression nameExpr, ImmutableArray<BoundArgument> arguments)
            : this(instance, new BoundRoutineName(nameExpr), arguments)
        {

        }

        public BoundInstanceFunctionCall(BoundExpression instance, BoundRoutineName name, ImmutableArray<BoundArgument> arguments)
            : base(arguments)
        {
            Debug.Assert(instance != null);
            Debug.Assert(name != null);

            _instance = instance;
            _name = name;
        }

        public BoundInstanceFunctionCall Update(BoundExpression instance, BoundRoutineName name, ImmutableArray<BoundArgument> arguments, ImmutableArray<IBoundTypeRef> typeArguments)
        {
            if (instance == _instance && name == _name && arguments == ArgumentsInSourceOrder && typeArguments == _typeargs)
            {
                return this;
            }
            else
            {
                return new BoundInstanceFunctionCall(instance, name, arguments) { TypeArguments = typeArguments }.WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitInstanceFunctionCall(this);
    }

    public partial class BoundStaticFunctionCall : BoundRoutineCall
    {
        public IBoundTypeRef TypeRef => _typeRef;
        readonly BoundTypeRef _typeRef;

        public override BoundExpression Instance => null;

        public BoundRoutineName Name => _name;
        readonly BoundRoutineName _name;

        public BoundStaticFunctionCall(IBoundTypeRef typeRef, BoundRoutineName name, ImmutableArray<BoundArgument> arguments)
            : base(arguments)
        {
            _typeRef = (BoundTypeRef)typeRef;
            _name = name;
        }

        public BoundStaticFunctionCall Update(IBoundTypeRef typeRef, BoundRoutineName name, ImmutableArray<BoundArgument> arguments, ImmutableArray<IBoundTypeRef> typeArguments)
        {
            if (typeRef == _typeRef && name == _name && arguments == ArgumentsInSourceOrder && typeArguments == _typeargs)
            {
                return this;
            }
            else
            {
                return new BoundStaticFunctionCall(typeRef, name, arguments) { TypeArguments = typeArguments }.WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitStaticFunctionCall(this);
    }

    /// <summary>
    /// Specialized <c>echo</c> function call.
    /// To be replaced with <c>Context.Echo</c> once overload resolution is implemented.
    /// </summary>
    public sealed partial class BoundEcho : BoundRoutineCall
    {
        public override BoundExpression Instance => null;

        public BoundEcho(ImmutableArray<BoundArgument> arguments)
            : base(arguments)
        {
        }

        public BoundEcho Update(ImmutableArray<BoundArgument> arguments)
        {
            if (arguments == ArgumentsInSourceOrder)
            {
                return this;
            }
            else
            {
                return new BoundEcho(arguments).WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitEcho(this);
    }

    /// <summary>
    /// Represents a string concatenation.
    /// </summary>
    public partial class BoundConcatEx : BoundRoutineCall
    {
        public override BoundExpression Instance => null;

        public BoundConcatEx(ImmutableArray<BoundArgument> arguments)
            : base(arguments)
        {
        }

        public BoundConcatEx Update(ImmutableArray<BoundArgument> arguments)
        {
            if (arguments == ArgumentsInSourceOrder)
            {
                return this;
            }
            else
            {
                return new BoundConcatEx(arguments).WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitConcat(this);
    }

    /// <summary>
    /// Direct new expression with a constructor call.
    /// </summary>
    public partial class BoundNewEx : BoundRoutineCall
    {
        /// <summary>
        /// Instantiated class type name.
        /// </summary>
        public IBoundTypeRef TypeRef => _typeref;
        readonly IBoundTypeRef _typeref;

        public override BoundExpression Instance => null;

        public BoundNewEx(IBoundTypeRef tref, ImmutableArray<BoundArgument> arguments)
            : base(arguments)
        {
            _typeref = tref;
        }

        public BoundNewEx Update(IBoundTypeRef tref, ImmutableArray<BoundArgument> arguments, ImmutableArray<IBoundTypeRef> typeArguments)
        {
            if (tref == _typeref && arguments == ArgumentsInSourceOrder && typeArguments == _typeargs)
            {
                return this;
            }
            else
            {
                return new BoundNewEx(tref, arguments) { TypeArguments = typeArguments }.WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitNew(this);
    }

    /// <summary>
    /// A script inclusion.
    /// </summary>
    public partial class BoundIncludeEx : BoundRoutineCall
    {
        public override BoundExpression Instance => null;

        /// <summary>
        /// Gets value indicating the target is resolved at compile time,
        /// so it will be called statically.
        /// </summary>
        public bool IsResolved => !Target.IsErrorMethodOrNull();

        /// <summary>
        /// In case the inclusion target is resolved, gets reference to the <c>Main</c> method of the included script.
        /// </summary>
        internal MethodSymbol Target
        {
            get
            {
                return TargetMethod;
            }
            set
            {
                TargetMethod = value;
            }
        }

        /// <summary>
        /// Type of inclusion, <c>include</c>, <c>require</c>, <c>include_once</c>, <c>require_once</c>.
        /// </summary>
        public InclusionTypes InclusionType { get; private set; }

        public BoundIncludeEx(BoundExpression target, InclusionTypes type)
            : base(ImmutableArray.Create(BoundArgument.Create(target)))
        {
            Debug.Assert(target.Access.IsRead);

            this.InclusionType = type;
        }

        public BoundIncludeEx Update(BoundExpression target, InclusionTypes type)
        {
            if (target == ArgumentsInSourceOrder[0].Value && type == InclusionType)
            {
                return this;
            }
            else
            {
                return new BoundIncludeEx(target, type).WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitInclude(this);
    }

    /// <summary>
    /// <c>exit</c> construct.
    /// </summary>
    public sealed partial class BoundExitEx : BoundRoutineCall
    {
        public override BoundExpression Instance => null;

        public BoundExitEx(BoundExpression value = null)
            : base(value != null ? ImmutableArray.Create(BoundArgument.Create(value)) : ImmutableArray<BoundArgument>.Empty)
        {
            Debug.Assert(value == null || value.Access.IsRead);
        }

        public BoundExitEx Update(ImmutableArray<BoundArgument> args)
        {
            if (args == ArgumentsInSourceOrder)
            {
                return this;
            }
            else
            {
                return new BoundExitEx(args.Length == 0 ? null : args[0].Value).WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitExit(this);
    }

    public sealed partial class BoundAssertEx : BoundRoutineCall
    {
        public override BoundExpression Instance => null;

        public BoundAssertEx(ImmutableArray<BoundArgument> arguments)
            : base(arguments)
        {

        }

        public BoundAssertEx Update(ImmutableArray<BoundArgument> arguments)
        {
            if (arguments == ArgumentsInSourceOrder)
            {
                return this;
            }
            else
            {
                return new BoundAssertEx(arguments).WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitAssert(this);
    }

    #endregion

    #region BoundLambda

    /// <summary>
    /// Anonymous function expression.
    /// </summary>
    public partial class BoundLambda : BoundExpression, IAnonymousFunctionOperation
    {
        /// <summary>
        /// Declared use variables.
        /// </summary>
        public ImmutableArray<BoundArgument> UseVars => _usevars;
        ImmutableArray<BoundArgument> _usevars;

        public IBlockOperation Body => (BoundLambdaMethod != null) ? BoundLambdaMethod.ControlFlowGraph.Start : null;

        public IMethodSymbol Signature => BoundLambdaMethod;

        /// <summary>
        /// Reference to associated lambda method symbol.
        /// Bound during analysis.
        /// </summary>
        internal SourceLambdaSymbol BoundLambdaMethod { get; set; }

        IMethodSymbol IAnonymousFunctionOperation.Symbol => BoundLambdaMethod;

        public BoundLambda(ImmutableArray<BoundArgument> usevars)
        {
            _usevars = usevars;
        }

        public BoundLambda Update(ImmutableArray<BoundArgument> usevars)
        {
            if (usevars == _usevars)
            {
                return this;
            }
            else
            {
                return new BoundLambda(usevars).WithContext(this);
            }
        }

        public override OperationKind Kind => OperationKind.AnonymousFunction;

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitLambda(this);

        public override void Accept(OperationVisitor visitor) => visitor.VisitAnonymousFunction(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument) => visitor.VisitAnonymousFunction(this, argument);
    }

    #endregion

    #region BoundEvalEx

    public partial class BoundEvalEx : BoundExpression
    {
        public override OperationKind Kind => OperationKind.None;

        public BoundExpression CodeExpression { get; internal set; }

        public BoundEvalEx(BoundExpression code)
        {
            Debug.Assert(code != null);
            this.CodeExpression = code;
        }

        public BoundEvalEx Update(BoundExpression code)
        {
            if (code == CodeExpression)
            {
                return this;
            }
            else
            {
                return new BoundEvalEx(code).WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitEval(this);

        public override void Accept(OperationVisitor visitor) => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument) => visitor.DefaultVisit(this, argument);
    }

    #endregion

    #region BoundLiteral

    public partial class BoundLiteral : BoundExpression, ILiteralOperation
    {
        public string Spelling => this.ConstantValue.Value?.ToString() ?? "NULL";

        public override OperationKind Kind => OperationKind.Literal;

        public override bool RequiresContext => false;

        public BoundLiteral(object value)
        {
            this.ConstantValue = value;
        }

        public BoundLiteral Update(object value)
        {
            if (value == ConstantValue.Value)
            {
                return this;
            }
            else
            {
                return new BoundLiteral(value).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitLiteral(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitLiteral(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitLiteral(this);
    }

    #endregion

    #region BoundCopyValue

    /// <summary>
    /// Deeply copies the expression's dereferenced value.
    /// </summary>
    public partial class BoundCopyValue : BoundExpression
    {
        public BoundCopyValue(BoundExpression expression)
        {
            this.Expression = expression ?? throw ExceptionUtilities.ArgumentNull(nameof(expression));
        }

        public BoundExpression Expression { get; }

        public override bool RequiresContext => this.Expression.RequiresContext;

        public override OperationKind Kind => OperationKind.None;

        public override void Accept(OperationVisitor visitor) => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument) => visitor.DefaultVisit(this, argument);

        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitCopyValue(this);

        internal BoundCopyValue Update(BoundExpression expression)
        {
            return expression == Expression ? this : new BoundCopyValue(expression).WithAccess(this.Access);
        }
    }

    #endregion

    #region BoundBinaryEx

    public sealed partial class BoundBinaryEx : BoundExpression, IBinaryOperation
    {
        public BinaryOperatorKind OperatorKind { get { throw new NotSupportedException(); } }

        public override OperationKind Kind => OperationKind.BinaryOperator;

        public override bool RequiresContext => Left.RequiresContext || Right.RequiresContext;

        public Ast.Operations Operation { get; private set; }

        public IMethodSymbol Operator { get; set; }

        IMethodSymbol IBinaryOperation.OperatorMethod => Operator;

        public BoundExpression Left { get; internal set; }

        public BoundExpression Right { get; internal set; }

        IOperation IBinaryOperation.LeftOperand => Left;

        IOperation IBinaryOperation.RightOperand => Right;

        bool IBinaryOperation.IsLifted => false;

        bool IBinaryOperation.IsChecked => false;

        bool IBinaryOperation.IsCompareText => false;

        public bool UsesOperatorMethod => this.Operator != null;

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitBinaryOperator(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitBinaryOperator(this, argument);

        internal BoundBinaryEx(BoundExpression left, BoundExpression right, Ast.Operations op)
        {
            this.Left = left;
            this.Right = right;
            this.Operation = op;
        }

        public BoundBinaryEx Update(BoundExpression left, BoundExpression right, Ast.Operations op)
        {
            if (left == Left && right == Right && op == Operation)
            {
                return this;
            }
            else
            {
                return new BoundBinaryEx(left, right, op).WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitBinaryExpression(this);
    }

    #endregion

    #region BoundUnaryEx

    public partial class BoundUnaryEx : BoundExpression, IUnaryOperation
    {
        public Ast.Operations Operation { get; private set; }

        public BoundExpression Operand { get; set; }

        public override OperationKind Kind => OperationKind.UnaryOperator;

        public override bool RequiresContext => Operation == Ast.Operations.Print || Operand.RequiresContext;

        IOperation IUnaryOperation.Operand => Operand;

        public IMethodSymbol OperatorMethod => null;

        bool IUnaryOperation.IsLifted => false;

        bool IUnaryOperation.IsChecked => false;

        public bool UsesOperatorMethod => OperatorMethod != null;

        public UnaryOperatorKind OperatorKind { get { throw new NotSupportedException(); } }

        public BoundUnaryEx(BoundExpression operand, Ast.Operations op)
        {
            Contract.ThrowIfNull(operand);
            this.Operand = operand;
            this.Operation = op;
        }

        public BoundUnaryEx Update(BoundExpression operand, Ast.Operations op)
        {
            if (operand == Operand && op == Operation)
            {
                return this;
            }
            else
            {
                return new BoundUnaryEx(operand, op).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitUnaryOperator(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitUnaryOperator(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitUnaryExpression(this);
    }

    #endregion

    #region BoundConvertEx

    /// <summary>
    /// Explicit conversion operation (cast operation).
    /// </summary>
    public sealed partial class BoundConversionEx : BoundExpression, IConversionOperation
    {
        public override OperationKind Kind => OperationKind.Conversion;

        IOperation IConversionOperation.Operand => Operand;

        IMethodSymbol IConversionOperation.OperatorMethod => Conversion.MethodSymbol;

        public override bool RequiresContext => Operand.RequiresContext || (TargetType is TypeRef.BoundPrimitiveTypeRef pt && pt.TypeCode == PhpTypeCode.String);

        public CommonConversion Conversion { get; set; }

        bool IConversionOperation.IsTryCast => false;

        public bool IsChecked { get; set; }

        public BoundExpression Operand { get; private set; }

        internal BoundTypeRef TargetType { get; private set; }

        internal BoundConversionEx(BoundExpression operand, BoundTypeRef targetType)
        {
            this.Operand = operand ?? throw new ArgumentNullException(nameof(operand));
            this.TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
        }

        internal BoundConversionEx Update(BoundExpression operand, BoundTypeRef targetType)
        {
            if (operand == this.Operand && targetType == this.TargetType)
            {
                return this;
            }
            else
            {
                return new BoundConversionEx(operand, targetType).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor) => visitor.VisitConversion(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument) => visitor.VisitConversion(this, argument);

        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitConversion(this);
    }

    #endregion

    #region BoundIncDecEx

    public partial class BoundIncDecEx : BoundCompoundAssignEx, IIncrementOrDecrementOperation
    {
        public override OperationKind Kind => IsIncrement ? OperationKind.Increment : OperationKind.Decrement;

        public bool IsIncrement { get; }

        public bool IsPostfix { get; }

        IMethodSymbol IIncrementOrDecrementOperation.OperatorMethod => null;

        IOperation IIncrementOrDecrementOperation.Target => Target;


        bool IIncrementOrDecrementOperation.IsLifted => false;

        bool IIncrementOrDecrementOperation.IsChecked => false;

        public BoundIncDecEx(BoundReferenceExpression target, bool isIncrement, bool isPostfix)
            : base(target, new BoundLiteral(1L).WithAccess(BoundAccess.Read), Ast.Operations.IncDec)
        {
            this.IsIncrement = isIncrement;
            this.IsPostfix = isPostfix;
        }

        public BoundIncDecEx Update(BoundReferenceExpression target, bool isIncrement, bool isPostfix)
        {
            if (target == Target && isIncrement == IsIncrement && isPostfix == IsPostfix)
            {
                return this;
            }
            else
            {
                return new BoundIncDecEx(target, isIncrement, isPostfix).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitIncrementOrDecrement(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitIncrementOrDecrement(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitIncDec(this);
    }

    #endregion

    #region BoundConditionalEx

    public partial class BoundConditionalEx : BoundExpression, IConditionalOperation
    {
        IOperation IConditionalOperation.Condition => Condition;
        IOperation IConditionalOperation.WhenFalse => IfFalse;
        IOperation IConditionalOperation.WhenTrue => IfTrue;
        bool IConditionalOperation.IsRef => false;

        public BoundExpression Condition { get; internal set; }
        public BoundExpression IfFalse { get; internal set; }
        public BoundExpression IfTrue { get; internal set; }

        public override OperationKind Kind => OperationKind.Conditional;

        public override bool RequiresContext => Condition.RequiresContext || IfTrue.RequiresContext || IfFalse.RequiresContext;

        public BoundConditionalEx(BoundExpression condition, BoundExpression iftrue, BoundExpression iffalse)
        {
            Contract.ThrowIfNull(condition);
            // Contract.ThrowIfNull(iftrue); // iftrue allowed to be null, condition used instead (condition ?: iffalse)
            Contract.ThrowIfNull(iffalse);

            this.Condition = condition;
            this.IfTrue = iftrue;
            this.IfFalse = iffalse;
        }

        public BoundConditionalEx Update(BoundExpression condition, BoundExpression ifTrue, BoundExpression ifFalse)
        {
            if (condition == Condition && ifTrue == IfTrue && ifFalse == IfFalse)
            {
                return this;
            }
            else
            {
                return new BoundConditionalEx(condition, ifTrue, ifFalse).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitConditional(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
        => visitor.VisitConditional(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitConditional(this);
    }

    #endregion

    #region BoundAssignEx, BoundCompoundAssignEx

    public partial class BoundAssignEx : BoundExpression, ISimpleAssignmentOperation
    {
        #region IAssignmentExpression

        IOperation IAssignmentOperation.Target => Target;

        IOperation IAssignmentOperation.Value => Value;

        bool ISimpleAssignmentOperation.IsRef => false;

        #endregion

        public override OperationKind Kind => OperationKind.SimpleAssignment;

        public BoundReferenceExpression Target { get; set; }

        public BoundExpression Value { get; set; }

        public BoundAssignEx(BoundReferenceExpression target, BoundExpression value)
        {
            this.Target = target;
            this.Value = value;
        }

        public BoundAssignEx Update(BoundReferenceExpression target, BoundExpression value)
        {
            if (target == Target && value == Value)
            {
                return this;
            }
            else
            {
                return new BoundAssignEx(target, value).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitSimpleAssignment(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitSimpleAssignment(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitAssign(this);
    }

    public partial class BoundCompoundAssignEx : BoundAssignEx, ICompoundAssignmentOperation
    {
        public BinaryOperatorKind OperatorKind { get { throw new NotSupportedException(); } }

        public override OperationKind Kind => OperationKind.CompoundAssignment;

        public IMethodSymbol OperatorMethod { get; set; }

        public bool UsesOperatorMethod => this.OperatorMethod != null;

        public Ast.Operations Operation { get; private set; }

        bool ICompoundAssignmentOperation.IsLifted => false;

        bool ICompoundAssignmentOperation.IsChecked => false;

        CommonConversion ICompoundAssignmentOperation.InConversion => throw new NotSupportedException();

        CommonConversion ICompoundAssignmentOperation.OutConversion => throw new NotSupportedException();

        public BoundCompoundAssignEx(BoundReferenceExpression target, BoundExpression value, Ast.Operations op)
            : base(target, value)
        {
            this.Operation = op;
        }

        public BoundCompoundAssignEx Update(BoundReferenceExpression target, BoundExpression value, Ast.Operations op)
        {
            if (target == Target && value == Value && op == Operation)
            {
                return this;
            }
            else
            {
                return new BoundCompoundAssignEx(target, value, op).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitCompoundAssignment(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitCompoundAssignment(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitCompoundAssign(this);
    }

    #endregion

    #region BoundReferenceExpression

    public abstract partial class BoundReferenceExpression : BoundExpression
    {
        /// <summary>
        /// Gets or sets value indicating the variable is used while it was not initialized in all code paths.
        /// </summary>
        public bool MaybeUninitialized { get; set; }
    }

    #endregion

    #region BoundVariableRef

    /// <summary>
    /// Direct or indirect variable name.
    /// </summary>
    [DebuggerDisplay("{DebugView,nq}")]
    public partial class BoundVariableName : BoundOperation, IPhpOperation
    {
        public VariableName NameValue => _nameValue;
        readonly VariableName _nameValue;

        public BoundExpression NameExpression => _nameExpression;
        readonly BoundExpression _nameExpression;

        string DebugView
        {
            get
            {
                return IsDirect ? _nameValue.ToString() : _nameExpression.ToString();
            }
        }

        public bool IsDirect => _nameExpression == null;

        public override OperationKind Kind => OperationKind.None;

        public Ast.LangElement PhpSyntax { get; set; }

        public BoundVariableName(VariableName name)
            : this(name, null)
        {
        }

        public BoundVariableName(BoundExpression nameExpr)
            : this(default, nameExpr)
        {
        }

        private BoundVariableName(VariableName name, BoundExpression nameExpr)
        {
            Debug.Assert(name.IsEmpty() != (nameExpr == null));
            _nameValue = name;
            _nameExpression = nameExpr;
        }

        public BoundVariableName Update(VariableName name, BoundExpression nameExpr)
        {
            if (name.NameEquals(_nameValue) && nameExpr == _nameExpression)
            {
                return this;
            }
            else
            {
                return new BoundVariableName(name, nameExpr);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.DefaultVisit(this, argument);

        public TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor)
            => visitor.VisitVariableName(this);
    }

    /// <summary>
    /// A variable reference that can be read or written to.
    /// </summary>
    public partial class BoundVariableRef : BoundReferenceExpression, ILocalReferenceOperation
    {
        readonly BoundVariableName _name;

        /// <summary>
        /// Name of the variable.
        /// </summary>
        public BoundVariableName Name => _name;

        /// <summary>
        /// Resolved variable source.
        /// </summary>
        public BoundVariable Variable { get; set; }

        public override OperationKind Kind => OperationKind.LocalReference;

        /// <summary>
        /// The type of variable before it gets accessed by this expression.
        /// </summary>
        internal TypeRefMask BeforeTypeRef { get; set; }

        /// <summary>
        /// Local in case of the variable is resolved local variable.
        /// </summary>
        ILocalSymbol ILocalReferenceOperation.Local => this.Variable?.Symbol as ILocalSymbol;

        bool ILocalReferenceOperation.IsDeclaration => throw new NotSupportedException();

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitLocalReference(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitLocalReference(this, argument);

        public BoundVariableRef(BoundVariableName name)
        {
            Debug.Assert(name != null);
            _name = name;
        }

        public BoundVariableRef Update(BoundVariableName name)
        {
            if (name == _name)
            {
                return this;
            }
            else
            {
                return new BoundVariableRef(name).WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitVariableRef(this);
    }

    /// <summary>
    /// A non-source synthesized variable reference that can be read or written to. 
    /// </summary>
    /// <remarks>
    /// Inheriting from <c>BoundVariableRef</c> is just a temporary measure. Do NOT take dependencies on anything but <c>IReferenceExpression</c>.
    /// </remarks>
    public partial class BoundTemporalVariableRef : BoundVariableRef
    {
        // TODO: Maybe change to visitor.VisitSyntheticLocalReferenceExpression
        public override void Accept(OperationVisitor visitor)
            => base.Accept(visitor);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => base.Accept(visitor, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitTemporalVariableRef(this);

        public BoundTemporalVariableRef(string name) : base(new BoundVariableName(new VariableName(name))) { }

        public BoundTemporalVariableRef Update(string name)
        {
            if (name == Name.NameValue.Value)
            {
                return this;
            }
            else
            {
                return new BoundTemporalVariableRef(name).WithContext(this);
            }
        }
    }

    #endregion

    #region BoundListEx

    /// <summary>
    /// PHP <c>list</c> expression that can be written to.
    /// </summary>
    public partial class BoundListEx : BoundReferenceExpression
    {
        public override OperationKind Kind => OperationKind.None;

        /// <summary>
        /// Bound target variables.
        /// </summary>
        public ImmutableArray<KeyValuePair<BoundExpression, BoundReferenceExpression>> Items { get; internal set; }

        public BoundListEx(IEnumerable<KeyValuePair<BoundExpression, BoundExpression>> items)
        {
            Debug.Assert(items != null);

            Items = items
                .Select(pair => new KeyValuePair<BoundExpression, BoundReferenceExpression>(pair.Key, (BoundReferenceExpression)pair.Value))
                .ToImmutableArray();
        }

        public BoundListEx(ImmutableArray<KeyValuePair<BoundExpression, BoundReferenceExpression>> items)
        {
            Debug.Assert(items != null);

            Items = items;
        }

        public BoundListEx Update(ImmutableArray<KeyValuePair<BoundExpression, BoundReferenceExpression>> items)
        {
            if (items == Items)
            {
                return this;
            }
            else
            {
                return new BoundListEx(items).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.DefaultVisit(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitList(this);
    }

    #endregion

    #region BoundFieldRef

    public partial class BoundFieldRef : BoundReferenceExpression, IFieldReferenceOperation
    {
        ISymbol IMemberReferenceOperation.Member => FieldSymbolOpt;

        IFieldSymbol IFieldReferenceOperation.Field => FieldSymbolOpt;

        IOperation IMemberReferenceOperation.Instance => Instance;

        bool IFieldReferenceOperation.IsDeclaration => throw new NotSupportedException();

        enum FieldType
        {
            InstanceField,
            StaticField,
            ClassConstant,
        }

        FieldType _type;

        BoundExpression _instanceExpr;      // in case of instance field
        IBoundTypeRef _containingType;      // in case of class constant or static field
        BoundVariableName _fieldName;       // field name

        public bool IsInstanceField => _type == FieldType.InstanceField;
        public bool IsStaticField => _type == FieldType.StaticField;
        public bool IsClassConstant => _type == FieldType.ClassConstant;

        /// <summary>
        /// In case of a non static field, gets its instance expression.
        /// </summary>
        public BoundExpression Instance
        {
            get => IsInstanceField ? _instanceExpr : null;
            set
            {
                if (IsInstanceField)
                    _instanceExpr = value;
                else
                    throw new InvalidOperationException();
            }
        }

        public IBoundTypeRef ContainingType => _containingType;

        public BoundVariableName FieldName { get => _fieldName; set => _fieldName = value; }

        public override OperationKind Kind => OperationKind.FieldReference;

        private BoundFieldRef(BoundExpression instance, IBoundTypeRef containingType, BoundVariableName name, FieldType fieldType)
        {
            Debug.Assert((instance == null) != (containingType == null));
            Debug.Assert((fieldType == FieldType.InstanceField) == (instance != null));

            _instanceExpr = instance;
            _containingType = containingType;
            _fieldName = name;
            _type = fieldType;
        }

        public static BoundFieldRef CreateInstanceField(BoundExpression instance, BoundVariableName name) => new BoundFieldRef(instance, null, name, FieldType.InstanceField);
        public static BoundFieldRef CreateStaticField(IBoundTypeRef type, BoundVariableName name) => new BoundFieldRef(null, type, name, FieldType.StaticField);
        public static BoundFieldRef CreateClassConst(IBoundTypeRef type, BoundVariableName name) => new BoundFieldRef(null, type, name, FieldType.ClassConstant);

        public BoundFieldRef Update(BoundExpression instance, IBoundTypeRef type, BoundVariableName name)
        {
            if (instance == _instanceExpr && type == _containingType && name == _fieldName)
            {
                return this;
            }
            else
            {
                return new BoundFieldRef(instance, type, name, _type).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitFieldReference(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitFieldReference(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitFieldRef(this);
    }

    #endregion

    #region BoundArrayEx

    public partial class BoundArrayEx : BoundExpression, IArrayCreationOperation
    {
        public class BoundArrayInitializer : BoundExpression, IArrayInitializerOperation
        {
            readonly BoundArrayEx _array;

            public override OperationKind Kind => OperationKind.ArrayInitializer;

            ImmutableArray<IOperation> IArrayInitializerOperation.ElementValues => _array._items.Select(x => x.Value).Cast<IOperation>().AsImmutable();

            public BoundArrayInitializer(BoundArrayEx array)
            {
                _array = array;
            }

            public BoundArrayInitializer Update(BoundArrayEx array)
            {
                if (array == _array)
                {
                    return this;
                }
                else
                {
                    return new BoundArrayInitializer(array).WithContext(this);
                }
            }

            public override void Accept(OperationVisitor visitor)
                => visitor.VisitArrayInitializer(this);

            public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
                => visitor.VisitArrayInitializer(this, argument);

            /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
            /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
            /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
            public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor)
            {
                throw new NotImplementedException();
            }
        }

        public override OperationKind Kind => OperationKind.ArrayCreation;

        public override bool RequiresContext => _items.Any(x => (x.Key != null && x.Key.RequiresContext) || x.Value.RequiresContext);

        ImmutableArray<IOperation> IArrayCreationOperation.DimensionSizes => ImmutableArray.Create<IOperation>(new BoundLiteral(_items.Length));

        IArrayInitializerOperation IArrayCreationOperation.Initializer => new BoundArrayInitializer(this);

        /// <summary>
        /// Array items.
        /// </summary>
        public ImmutableArray<KeyValuePair<BoundExpression, BoundExpression>> Items { get => _items; internal set => _items = value; }
        ImmutableArray<KeyValuePair<BoundExpression, BoundExpression>> _items;

        public BoundArrayEx(IEnumerable<KeyValuePair<BoundExpression, BoundExpression>> items)
        {
            _items = items.ToImmutableArray();
        }

        public BoundArrayEx Update(ImmutableArray<KeyValuePair<BoundExpression, BoundExpression>> items)
        {
            if (items == _items)
            {
                return this;
            }
            else
            {
                return new BoundArrayEx(items).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitArrayCreation(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitArrayCreation(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitArray(this);
    }

    #endregion

    #region BoundArrayItemEx

    /// <summary>
    /// Array item access.
    /// </summary>
    public partial class BoundArrayItemEx : BoundReferenceExpression, IArrayElementReferenceOperation
    {
        public BoundExpression Array
        {
            get { return _array; }
            set { _array = value; }
        }
        BoundExpression _array;

        public BoundExpression Index
        {
            get { return _index; }
            set { _index = value; }
        }
        BoundExpression _index;

        public override OperationKind Kind => OperationKind.ArrayElementReference;

        IOperation IArrayElementReferenceOperation.ArrayReference => _array;

        ImmutableArray<IOperation> IArrayElementReferenceOperation.Indices
            => (_index != null) ? ImmutableArray.Create((IOperation)_index) : ImmutableArray<IOperation>.Empty;

        public BoundArrayItemEx(BoundExpression array, BoundExpression index)
        {
            Contract.ThrowIfNull(array);

            _array = array;
            _index = index;
        }

        public BoundArrayItemEx Update(BoundExpression array, BoundExpression index)
        {
            if (array == _array && index == _index)
            {
                return this;
            }
            else
            {
                return new BoundArrayItemEx(array, index).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitArrayElementReference(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitArrayElementReference(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitArrayItem(this);
    }

    #endregion

    #region BoundInstanceOfEx

    public partial class BoundInstanceOfEx : BoundExpression, IIsTypeOperation
    {
        #region IIsExpression

        IOperation IIsTypeOperation.ValueOperand => Operand;

        ITypeSymbol IIsTypeOperation.TypeOperand => AsType?.Type;

        bool IIsTypeOperation.IsNegated => false;

        #endregion

        /// <summary>
        /// The value to be checked.
        /// </summary>
        public BoundExpression Operand { get; internal set; }

        /// <summary>
        /// The type.
        /// </summary>
        public IBoundTypeRef AsType { get; private set; }

        public BoundInstanceOfEx(BoundExpression operand, IBoundTypeRef tref)
        {
            Contract.ThrowIfNull(operand);

            this.Operand = operand;
            this.AsType = tref;
        }

        public BoundInstanceOfEx Update(BoundExpression operand, IBoundTypeRef tref)
        {
            if (operand == Operand && tref == AsType)
            {
                return this;
            }
            else
            {
                return new BoundInstanceOfEx(operand, tref).WithContext(this);
            }
        }

        public override OperationKind Kind => OperationKind.IsType;

        public override void Accept(OperationVisitor visitor)
            => visitor.VisitIsType(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.VisitIsType(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitInstanceOf(this);
    }

    #endregion

    #region BoundGlobalConst

    public partial class BoundGlobalConst : BoundExpression
    {
        public override OperationKind Kind => OperationKind.None;

        /// <summary>
        /// Constant name.
        /// </summary>
        public QualifiedName Name { get; private set; }

        /// <summary>
        /// Alternative constant name if <see cref="Name"/> is not resolved.
        /// </summary>
        public QualifiedName? FallbackName { get; private set; }

        public BoundGlobalConst(QualifiedName name, QualifiedName? fallbackName)
        {
            this.Name = name;
            this.FallbackName = fallbackName;
        }

        public BoundGlobalConst Update(QualifiedName name, QualifiedName? fallbackName)
        {
            if (name == Name && fallbackName == FallbackName)
            {
                return this;
            }
            else
            {
                return new BoundGlobalConst(name, fallbackName).WithContext(this);
            }
        }

        /// <summary>
        /// In case the constant is resolved to a place.
        /// </summary>
        internal IBoundReference _boundExpressionOpt;

        public override void Accept(OperationVisitor visitor)
            => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.DefaultVisit(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitGlobalConstUse(this);
    }

    #endregion

    #region BoundPseudoConst

    public partial class BoundPseudoConst : BoundExpression
    {
        public override OperationKind Kind => OperationKind.None;

        public Ast.PseudoConstUse.Types ConstType { get; private set; }

        public BoundPseudoConst(Ast.PseudoConstUse.Types type)
        {
            this.ConstType = type;
        }

        public BoundPseudoConst Update(Ast.PseudoConstUse.Types type)
        {
            if (type == ConstType)
            {
                return this;
            }
            else
            {
                return new BoundPseudoConst(type).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.DefaultVisit(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitPseudoConstUse(this);
    }

    #endregion

    #region BoundPseudoClassConst

    public partial class BoundPseudoClassConst : BoundExpression
    {
        public Ast.PseudoClassConstUse.Types ConstType { get; private set; }

        public override OperationKind Kind => OperationKind.None;

        public IBoundTypeRef TargetType { get; private set; }

        public BoundPseudoClassConst(IBoundTypeRef targetType, Ast.PseudoClassConstUse.Types type)
        {
            this.TargetType = targetType;
            this.ConstType = type;
        }

        public BoundPseudoClassConst Update(IBoundTypeRef targetType, Ast.PseudoClassConstUse.Types type)
        {
            if (targetType == TargetType && type == ConstType)
            {
                return this;
            }
            else
            {
                return new BoundPseudoClassConst(targetType, type).WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitPseudoClassConstUse(this);

        public override void Accept(OperationVisitor visitor) => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument) => visitor.DefaultVisit(this, argument);
    }

    #endregion

    #region BoundIsSetEx, BoundUnsetEx, BoundIsEmptyEx

    public partial class BoundIsEmptyEx : BoundExpression
    {
        public override OperationKind Kind => OperationKind.None;

        /// <summary>
        /// Reference to be checked if it is set.
        /// </summary>
        public BoundExpression Operand { get; set; }

        public BoundIsEmptyEx(BoundExpression expression)
        {
            this.Operand = expression;
        }

        public BoundIsEmptyEx Update(BoundExpression expression)
        {
            if (expression == Operand)
            {
                return this;
            }
            else
            {
                return new BoundIsEmptyEx(expression).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.DefaultVisit(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitIsEmpty(this);
    }

    public partial class BoundIsSetEx : BoundExpression
    {
        public override OperationKind Kind => OperationKind.None;

        /// <summary>
        /// Reference to be checked if it is set.
        /// </summary>
        public BoundReferenceExpression VarReference { get; set; }

        public BoundIsSetEx(BoundReferenceExpression varref)
        {
            this.VarReference = varref;
        }

        public BoundIsSetEx Update(BoundReferenceExpression varref)
        {
            if (varref == VarReference)
            {
                return this;
            }
            else
            {
                return new BoundIsSetEx(varref).WithContext(this);
            }
        }

        public override void Accept(OperationVisitor visitor)
            => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.DefaultVisit(this, argument);

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor) => visitor.VisitIsSet(this);
    }

    #endregion

    #region BoundYieldEx, BoundYieldFromEx

    /// <summary>
    /// Represents a reference to an item sent to the generator.
    /// </summary>
    public partial class BoundYieldEx : BoundExpression
    {
        public override OperationKind Kind => OperationKind.FieldReference;

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor)
            => visitor.VisitYieldEx(this);

        public override void Accept(OperationVisitor visitor)
            => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.DefaultVisit(this, argument);
    }

    /// <summary>
    /// Represents a return from `yield from` expression.
    /// That is the value returned from eventual `Generator` being yielded from.
    /// </summary>
    public partial class BoundYieldFromEx : BoundExpression
    {
        public override OperationKind Kind => OperationKind.FieldReference;

        public BoundExpression Operand { get; internal set; }

        public BoundYieldFromEx(BoundExpression expression)
        {
            Operand = expression;
        }

        public BoundYieldFromEx Update(BoundExpression expression)
        {
            if (expression == Operand)
            {
                return this;
            }
            else
            {
                return new BoundYieldFromEx(expression).WithContext(this);
            }
        }

        /// <summary>Invokes corresponding <c>Visit</c> method on given <paramref name="visitor"/>.</summary>
        /// <param name="visitor">A reference to a <see cref="PhpOperationVisitor{TResult}"/> instance. Cannot be <c>null</c>.</param>
        /// <returns>The value returned by the <paramref name="visitor"/>.</returns>
        public override TResult Accept<TResult>(PhpOperationVisitor<TResult> visitor)
            => visitor.VisitYieldFromEx(this);

        public override void Accept(OperationVisitor visitor)
            => visitor.DefaultVisit(this);

        public override TResult Accept<TArgument, TResult>(OperationVisitor<TArgument, TResult> visitor, TArgument argument)
            => visitor.DefaultVisit(this, argument);
    }

    #endregion
}
