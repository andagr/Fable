module Fable.Transforms.Replacements

open Fable
open Fable.AST
open Fable.AST.Fable
open Microsoft.FSharp.Compiler.SourceCodeServices

type BuiltinType =
    | BclGuid
    | BclTimeSpan
    | BclDateTime
    | BclDateTimeOffset
    | BclInt64
    | BclUInt64
    | BclBigInt

let (|Builtin|_|) = function
    | DeclaredType(ent,_) ->
        match ent.TryFullName with
        | Some Types.guid -> Some BclGuid
        | Some Types.timespan -> Some BclTimeSpan
        | Some Types.datetime -> Some BclDateTime
        | Some Types.datetimeOffset -> Some BclDateTimeOffset
        | Some "System.Int64" -> Some BclInt64
        | Some "System.UInt64" -> Some BclUInt64
        | Some (Naming.StartsWith "Microsoft.FSharp.Core.int64" _) -> Some BclInt64
        | Some "System.Numerics.BigInteger" -> Some BclBigInt
        | _ -> None
        | _ -> None
    | _ -> None

let (|Integer|Float|) = function
    | Int8 | UInt8 | Int16 | UInt16 | Int32 | UInt32 -> Integer
    | Float32 | Float64 | Decimal -> Float

let coreModFor = function
    | BclGuid -> "String"
    | BclTimeSpan -> "Long"
    | BclDateTime -> "Date"
    | BclDateTimeOffset -> "DateOffset"
    | BclInt64 -> "Long"
    | BclUInt64 -> "Long"
    | BclBigInt -> "BigInt"

let ccall r t info coreModule coreMember args =
    let callee = Import(coreMember, coreModule, CoreLib, Any)
    Operation(Call(callee, None, args, info), t, r)

let ccall_ t coreModule coreMember args =
    let callee = Import(coreMember, coreModule, CoreLib, Any)
    makeCallNoInfo None t callee args

let makeLongInt t signed (x: uint64) =
    let lowBits = NumberConstant (float (uint32 x), Float64)
    let highBits = NumberConstant (float (x >>> 32), Float64)
    let unsigned = BoolConstant (not signed)
    let args = [Value lowBits; Value highBits; Value unsigned]
    ccall_ t "Long" "fromBits" args

let makeFloat32 (x: float32) =
    let args = [NumberConstant (float x, Float32) |> Value]
    let callee = makeUntypedFieldGet (makeIdentExpr "Math") "fround"
    makeCallNoInfo None (Number Float32) callee args

let makeTypeConst (typ: Type) (value: obj) =
    match typ, value with
    // Long Integer types
    | Builtin BclInt64, (:? int64 as x) -> makeLongInt typ true (uint64 x)
    | Builtin BclUInt64, (:? uint64 as x) -> makeLongInt typ false x
    // Decimal type
    | Number Decimal, (:? decimal as x) -> makeDecConst x
    // Short Float type
    | Number Float32, (:? float32 as x) -> makeFloat32 x
    | Boolean, (:? bool as x) -> BoolConstant x |> Value
    | String, (:? string as x) -> StringConstant x |> Value
    | Char, (:? char as x) -> StringConstant (string x) |> Value
    // Integer types
    | Number UInt8, (:? byte as x) -> NumberConstant (float x, UInt8) |> Value
    | Number Int8, (:? sbyte as x) -> NumberConstant (float x, Int8) |> Value
    | Number Int16, (:? int16 as x) -> NumberConstant (float x, Int16) |> Value
    | Number UInt16, (:? uint16 as x) -> NumberConstant (float x, UInt16) |> Value
    | Number Int32, (:? int as x) -> NumberConstant (float x, Int32) |> Value
    | Number UInt32, (:? uint32 as x) -> NumberConstant (float x, UInt32) |> Value
    // Float types
    | Number Float64, (:? float as x) -> NumberConstant (float x, Float64) |> Value
    // Enums
    | EnumType _, (:? int64)
    | EnumType _, (:? uint64) -> failwith "int64 enums are not supported"
    | EnumType(_, name), (:? byte as x) -> Enum(NumberEnum(int x), name) |> Value
    | EnumType(_, name), (:? sbyte as x) -> Enum(NumberEnum(int x), name) |> Value
    | EnumType(_, name), (:? int16 as x) -> Enum(NumberEnum(int x), name) |> Value
    | EnumType(_, name), (:? uint16 as x) -> Enum(NumberEnum(int x), name) |> Value
    | EnumType(_, name), (:? int as x) -> Enum(NumberEnum(int x), name) |> Value
    | EnumType(_, name), (:? uint32 as x) -> Enum(NumberEnum(int x), name) |> Value
    // TODO: Regex
    | Unit, _ -> UnitConstant |> Value
    // Arrays with small data type (ushort, byte) are represented
    // in F# AST as BasicPatterns.Const
    | Array (Number kind), (:? (byte[]) as arr) ->
        let values = arr |> Array.map (fun x -> NumberConstant (float x, kind) |> Value) |> Seq.toList
        NewArray (ArrayValues values, Number kind) |> Value
    | Array (Number kind), (:? (uint16[]) as arr) ->
        let values = arr |> Array.map (fun x -> NumberConstant (float x, kind) |> Value) |> Seq.toList
        NewArray (ArrayValues values, Number kind) |> Value
    | _ -> failwithf "Unexpected type %A, literal %O" typ value

let getTypedArrayName (com: ICompiler) numberKind =
    match numberKind with
    | Int8 -> "Int8Array"
    | UInt8 -> if com.Options.clampByteArrays then "Uint8ClampedArray" else "Uint8Array"
    | Int16 -> "Int16Array"
    | UInt16 -> "Uint16Array"
    | Int32 -> "Int32Array"
    | UInt32 -> "Uint32Array"
    | Float32 -> "Float32Array"
    | Float64 | Decimal -> "Float64Array"

let rec makeTypeTest com range expr (typ: Type) =
    let jsTypeof (primitiveType: string) expr =
        let typof = makeUnOp None String expr UnaryTypeof
        makeEqOp range typof (makeStrConst primitiveType) BinaryEqualStrict
    let jsInstanceof (cons: Expr) expr =
        makeBinOp range Boolean expr cons BinaryInstanceOf
    match typ with
    | Any -> makeBoolConst true
    | Unit -> makeEqOp range expr (Value <| Null Any) BinaryEqual
    | Boolean -> jsTypeof "boolean" expr
    | Char | String _ -> jsTypeof "string" expr
    | FunctionType _ -> jsTypeof "function" expr
    | Number _ | EnumType _ -> jsTypeof "number" expr
    | Regex ->
        jsInstanceof (makeIdentExpr "RegExp") expr
    | Builtin (BclInt64 | BclUInt64) ->
        jsInstanceof (makeCoreRef Any "Long" "default") expr
    | Builtin BclBigInt ->
        jsInstanceof (makeCoreRef Any "BigInt" "default") expr
    | Array _ | Tuple _ | List _ ->
        ccall_ Boolean "Util" "isArray" [expr]
    | DeclaredType (ent, _) ->
        failwith "TODO: DeclaredType type test"
        // if ent.IsClass
        // then jsInstanceof (TypeRef ent) expr
        // else "Cannot type test interfaces, records or unions"
        //      |> addErrorAndReturnNull com fileName range
    | Option _ | GenericParam _ | ErasedUnion _ ->
        "Cannot type test options, generic parameters or erased unions"
        |> addErrorAndReturnNull com range

// TODO: Check special cases: custom operators, bigint, dates, etc
let applyOp (com: ICompiler) r t opName genArgs args =
    let (|CustomOp|_|) com opName genArgs (ent: FSharpEntity) =
        // The last generic argument is the return type, remove it
        match genArgs with
        | left::right::_ -> Some [left; right]
        | operand::_ -> Some [operand]
        | _ -> None
        |> Option.bind (FSharp2Fable.TypeHelpers.tryFindMember com ent opName false)
    let unOp operator operand =
        Operation(UnaryOperation(operator, operand), t, r)
    let binOp op left right =
        Operation(BinaryOperation(op, left, right), t, r)
    let logicOp op left right =
        Operation(LogicalOperation(op, left, right), Boolean, r)
    let nativeOp opName genArgs args =
        match opName, args with
        | Operators.addition, [left; right] -> binOp BinaryPlus left right
        | Operators.subtraction, [left; right] -> binOp BinaryMinus left right
        | Operators.multiply, [left; right] -> binOp BinaryMultiply left right
        | Operators.division, [left; right] ->
            let div = binOp BinaryDivide left right
            match genArgs with
            // Floor result of integer divisions (see #172)
            // Apparently ~~ is faster than Math.floor (see https://coderwall.com/p/9b6ksa/is-faster-than-math-floor)
            | Number Integer::_ -> unOp UnaryNotBitwise div |> unOp UnaryNotBitwise
            | _ -> div
        | Operators.modulus, [left; right] -> binOp BinaryModulus left right
        | Operators.leftShift, [left; right] -> binOp BinaryShiftLeft left right
        | Operators.rightShift, [left; right] ->
            match genArgs with
            | Number UInt32::_ -> binOp BinaryShiftRightZeroFill left right // See #646
            | _ -> binOp BinaryShiftRightSignPropagating left right
        | Operators.bitwiseAnd, [left; right] -> binOp BinaryAndBitwise left right
        | Operators.bitwiseOr, [left; right] -> binOp BinaryOrBitwise left right
        | Operators.exclusiveOr, [left; right] -> binOp BinaryXorBitwise left right
        | Operators.booleanAnd, [left; right] -> logicOp LogicalAnd left right
        | Operators.booleanOr, [left; right] -> logicOp LogicalOr left right
        | Operators.logicalNot, [operand] -> unOp UnaryNotBitwise operand
        | Operators.unaryNegation, [operand] -> unOp UnaryMinus operand
        | _ -> "Unknown operator: " + opName |> addErrorAndReturnNull com r
    match genArgs with
    // TODO: Builtin types
    | DeclaredType(CustomOp com opName genArgs m, _)::_
    | _::DeclaredType(CustomOp com opName genArgs m, _)::_ ->
        let callee = FSharp2Fable.Util.memberRef com genArgs m
        makeCallNoInfo r t callee args
    | _ -> nativeOp opName genArgs args

let operators (com: ICompiler) r t callee args (info: CallInfo) (extraInfo: ExtraCallInfo) =
    if Set.contains extraInfo.CompiledName Operators.standard
    then applyOp com r t extraInfo.CompiledName extraInfo.GenericArgs args |> Some
    else None

let fableCoreLib com r t callee args info (extraInfo: ExtraCallInfo) =
    match extraInfo.CompiledName with
    | "AreEqual" -> ccall r t info "Assert" "equal" args |> Some
    | _ -> None

let tryField returnTyp ownerTyp fieldName =
    match ownerTyp, fieldName with
    | Number Decimal, "Zero" -> makeDecConst 0M |> Some
    | Number Decimal, "One" -> makeDecConst 1M |> Some
    | String, "Emtpy" -> makeStrConst "" |> Some
    | Builtin BclGuid, "Empty" -> makeStrConst "00000000-0000-0000-0000-000000000000" |> Some
    | Builtin BclTimeSpan, "Zero" -> makeIntConst 0 |> Some
    | Builtin BclDateTime, ("MaxValue" | "MinValue") ->
        ccall_ returnTyp (coreModFor BclDateTime) (Naming.lowerFirst fieldName) [] |> Some
    | Builtin BclDateTimeOffset, ("MaxValue" | "MinValue") ->
        ccall_ returnTyp (coreModFor BclDateTimeOffset) (Naming.lowerFirst fieldName) [] |> Some
    | _ -> None

let tryCall (com: ICompiler) r t (callee: Expr option) (args: Expr list)
                            (info: CallInfo) (extraInfo: ExtraCallInfo) =
    let ownerName = extraInfo.FullName.Substring(0, extraInfo.FullName.LastIndexOf('.'))
    match ownerName with
    | "System.Math"
    | "Microsoft.FSharp.Core.Operators"
    | "Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators"
    | "Microsoft.FSharp.Core.ExtraTopLevelOperators" -> operators com r t callee args info extraInfo
    | Naming.StartsWith "Fable.Core." _ -> fableCoreLib com r t callee args info extraInfo
    | _ -> None
//         | Naming.EndsWith "Exception" _ -> exceptions com info
//         | "System.Object" -> objects com info
//         | "System.Timers.ElapsedEventArgs" -> info.callee // only signalTime is available here
//         | "System.Char" -> chars com info
//         | "System.Enum" -> enums com info
//         | "System.String"
//         | "Microsoft.FSharp.Core.StringModule" -> strings com info
//         | "Microsoft.FSharp.Core.PrintfModule"
//         | "Microsoft.FSharp.Core.PrintfFormat" -> fsFormat com info
//         | "System.BitConverter" -> bitConvert com info
//         | "System.Int32" -> parse com info false
//         | "System.Single"
//         | "System.Double" -> parse com info true
//         | "System.Convert" -> convert com info
//         | "System.Console" -> console com info
//         | "System.Decimal" -> decimals com info
//         | "System.Diagnostics.Debug"
//         | "System.Diagnostics.Debugger" -> debug com info
//         | "System.DateTime"
//         | "System.DateTimeOffset" -> dates com info
//         | "System.TimeSpan" -> timeSpans com info
//         | "System.Environment" -> systemEnv com info
//         | "System.Globalization.CultureInfo" -> globalization com info
//         | "System.Action" | "System.Func"
//         | "Microsoft.FSharp.Core.FSharpFunc"
//         | "Microsoft.FSharp.Core.OptimizedClosures.FSharpFunc" -> funcs com info
//         | "System.Random" -> random com info
//         | "Microsoft.FSharp.Core.FSharpOption"
//         | "Microsoft.FSharp.Core.OptionModule" -> options com info
//         | "System.Threading.CancellationToken"
//         | "System.Threading.CancellationTokenSource" -> cancels com info
//         | "Microsoft.FSharp.Core.FSharpRef" -> references com info
//         | "System.Activator" -> activator com info
//         | "Microsoft.FSharp.Core.LanguagePrimitives.ErrorStrings" -> errorStrings com info
//         | "Microsoft.FSharp.Core.LanguagePrimitives" -> languagePrimitives com info
//         | "Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicFunctions"
//         | "Microsoft.FSharp.Core.Operators.OperatorIntrinsics" -> intrinsicFunctions com info
//         | "System.Text.RegularExpressions.Capture"
//         | "System.Text.RegularExpressions.Match"
//         | "System.Text.RegularExpressions.Group"
//         | "System.Text.RegularExpressions.MatchCollection"
//         | "System.Text.RegularExpressions.GroupCollection"
//         | "System.Text.RegularExpressions.Regex" -> regex com info
//         | "System.Collections.Generic.IEnumerable"
//         | "System.Collections.IEnumerable" -> enumerable com info
//         | "System.Collections.Generic.Dictionary"
//         | "System.Collections.Generic.IDictionary" -> dictionaries com info
//         | "System.Collections.Generic.HashSet"
//         | "System.Collections.Generic.ISet" -> hashSets com info
//         | "System.Collections.Generic.KeyValuePair" -> keyValuePairs com info
//         | "System.Collections.Generic.Dictionary.KeyCollection"
//         | "System.Collections.Generic.Dictionary.ValueCollection"
//         | "System.Collections.Generic.ICollection" -> collectionsSecondPass com info Seq
//         | "System.Array"
//         | "System.Collections.Generic.List"
//         | "System.Collections.Generic.IList" -> collectionsSecondPass com info Array
//         | "Microsoft.FSharp.Collections.ArrayModule" -> collectionsFirstPass com info Array
//         | "Microsoft.FSharp.Collections.FSharpList"
//         | "Microsoft.FSharp.Collections.ListModule" -> collectionsFirstPass com info List
//         | "Microsoft.FSharp.Collections.SeqModule" -> collectionsSecondPass com info Seq
//         | "Microsoft.FSharp.Collections.FSharpMap"
//         | "Microsoft.FSharp.Collections.MapModule"
//         | "Microsoft.FSharp.Collections.FSharpSet"
//         | "Microsoft.FSharp.Collections.SetModule" -> mapAndSets com info
//         | "System.Type" -> types com info
//         | "Microsoft.FSharp.Core.Operators.Unchecked" -> unchecked com info
//         | "Microsoft.FSharp.Control.FSharpMailboxProcessor"
//         | "Microsoft.FSharp.Control.FSharpAsyncReplyChannel" -> mailbox com info
//         | "Microsoft.FSharp.Control.FSharpAsync" -> asyncs com info
//         | "System.Guid" -> guids com info
//         | "System.Uri" -> uris com info
//         | "System.Lazy" | "Microsoft.FSharp.Control.Lazy"
//         | "Microsoft.FSharp.Control.LazyExtensions" -> laziness com info
//         | "Microsoft.FSharp.Control.CommonExtensions" -> controlExtensions com info
//         | "System.Numerics.BigInteger"
//         | "Microsoft.FSharp.Core.NumericLiterals.NumericLiteralI" -> bigint com info
//         | "Microsoft.FSharp.Reflection.FSharpType" -> fsharpType com info info.memberName
//         | "Microsoft.FSharp.Reflection.FSharpValue" -> fsharpValue com info info.memberName
//         | "Microsoft.FSharp.Reflection.FSharpReflectionExtensions" ->
//             // In netcore F# Reflection methods become extensions
//             // with names like `FSharpType.GetExceptionFields.Static`
//             let isFSharpType = info.memberName.StartsWith("fSharpType")
//             let methName = info.memberName |> Naming.extensionMethodName |> Naming.lowerFirst
//             if isFSharpType
//             then fsharpType com info methName
//             else fsharpValue com info methName
//         | "Microsoft.FSharp.Reflection.UnionCaseInfo"
//         | "System.Reflection.PropertyInfo"
//         | "System.Reflection.MemberInfo" ->
//             match info.callee, info.memberName with
//             | _, "getFields" -> icall info "getUnionFields" |> Some
//             | Some c, "name" -> ccall info "Reflection" "getName" [c] |> Some
//             | Some c, ("tag" | "propertyType") ->
//                 let prop =
//                     if info.memberName = "tag" then "index" else info.memberName
//                     |> Fable.StringConst |> Fable.Value
//                 makeGet info.range info.returnType c prop |> Some
//             | _ -> None
//         | _ -> None
