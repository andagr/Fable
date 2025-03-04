module Fable.Transforms.FableTransforms

open Fable
open Fable.AST.Fable

let getSubExpressions = function
    | Unresolved _ -> []
    | IdentExpr _ -> []
    | TypeCast(e,_,_) -> [e]
    | Import _ -> []
    | Value(kind,_) ->
        match kind with
        | ThisValue _ | BaseValue _
        | TypeInfo _ | Null _ | UnitConstant
        | BoolConstant _ | CharConstant _ | StringConstant _
        | NumberConstant _ | RegexConstant _ -> []
        | EnumConstant(e, _) -> [e]
        | NewOption(e, _) -> Option.toList e
        | NewTuple exprs -> exprs
        | NewArray(exprs, _) -> exprs
        | NewArrayFrom(e, _) -> [e]
        | NewList(ht, _) ->
            match ht with Some(h,t) -> [h;t] | None -> []
        | NewRecord(exprs, _, _) -> exprs
        | NewAnonymousRecord(exprs, _, _) -> exprs
        | NewUnion(exprs, _, _, _) -> exprs
    | Test(e, _, _) -> [e]
    | Curry(e, _, _, _) -> [e]
    | Lambda(_, body, _) -> [body]
    | Delegate(_, body, _) -> [body]
    | ObjectExpr(members, _, baseCall) ->
        let members = members |> List.map (fun m -> m.Body)
        match baseCall with Some b -> b::members | None -> members
    | CurriedApply(callee, args, _, _) -> callee::args
    | Call(e1, info, _, _) -> e1 :: (Option.toList info.ThisArg) @ info.Args
    | Emit(info, _, _) -> (Option.toList info.CallInfo.ThisArg) @ info.CallInfo.Args
    | Operation(kind, _, _) ->
        match kind with
        | Unary(_, operand) -> [operand]
        | Binary(_, left, right) -> [left; right]
        | Logical(_, left, right) -> [left; right]
    | Get(e, kind, _, _) ->
        match kind with
        | ListHead | ListTail | OptionValue | TupleIndex _ | UnionTag
        | UnionField _ | ByKey(FieldKey _) -> [e]
        | ByKey(ExprKey e2) -> [e; e2]
    | Sequential exprs -> exprs
    | Let(_, value, body) -> [value; body]
    | LetRec(bs, body) -> (List.map snd bs) @ [body]
    | IfThenElse(cond, thenExpr, elseExpr, _) -> [cond; thenExpr; elseExpr]
    | Set(e, kind, v, _) ->
        match kind with
        | Some(ExprKey e2) -> [e; e2; v]
        | Some(FieldKey _) | None -> [e; v]
    | WhileLoop(e1, e2, _) -> [e1; e2]
    | ForLoop(_, e1, e2, e3, _, _) -> [e1; e2; e3]
    | TryCatch(body, catch, finalizer, _) ->
        match catch with
        | Some(_,c) -> body::c::(Option.toList finalizer)
        | None -> body::(Option.toList finalizer)
    | DecisionTree(expr, targets) -> expr::(List.map snd targets)
    | DecisionTreeSuccess(_, boundValues, _) -> boundValues

let deepExists (f: Expr -> bool) expr =
    let rec deepExistsInner (exprs: ResizeArray<Expr>) =
        let mutable found = false
        let subExprs = ResizeArray()
        for e in exprs do
            if not found then
                subExprs.AddRange(getSubExpressions e)
                found <- f e
        if found then true
        elif subExprs.Count > 0 then deepExistsInner subExprs
        else false
    ResizeArray [|expr|] |> deepExistsInner

let isIdentUsed identName expr =
    expr |> deepExists (function
        | IdentExpr i -> i.Name = identName
        | _ -> false)

let isIdentCaptured identName expr =
    let rec loop isClosure exprs =
        match exprs with
        | [] -> false
        | expr::restExprs ->
            match expr with
            | IdentExpr i when i.Name = identName -> isClosure
            | Lambda(_,body,_) -> loop true [body] || loop isClosure restExprs
            | Delegate(_,body,_) -> loop true [body] || loop isClosure restExprs
            | ObjectExpr(members, _, baseCall) ->
                let memberExprs = members |> List.map (fun m -> m.Body)
                loop true memberExprs || loop isClosure (Option.toList baseCall @ restExprs)
            | e ->
                let sub = getSubExpressions e
                loop isClosure (sub @ restExprs)
    loop false [expr]

let replaceValues replacements expr =
    if Map.isEmpty replacements
    then expr
    else expr |> visitFromInsideOut (function
        | IdentExpr id as e ->
            match Map.tryFind id.Name replacements with
            | Some e -> e
            | None -> e
        | e -> e)

let replaceNames replacements expr =
    if Map.isEmpty replacements
    then expr
    else expr |> visitFromInsideOut (function
        | IdentExpr id as e ->
            match Map.tryFind id.Name replacements with
            | Some name -> { id with Name=name } |> IdentExpr
            | None -> e
        | e -> e)

let countReferences limit identName body =
    let mutable count = 0
    body |> deepExists (function
        | IdentExpr id2 when id2.Name = identName ->
            count <- count + 1
            count > limit
        | _ -> false) |> ignore
    count

let noSideEffectBeforeIdent identName expr =
    let mutable sideEffect = false
    let orSideEffect found =
        if found then true
        else
            sideEffect <- true
            true

    let rec findIdentOrSideEffect = function
        | Unresolved _ -> false
        | IdentExpr id ->
            if id.Name = identName then true
            elif id.IsMutable then
                sideEffect <- true
                true
            else false
        // If the field is mutable we cannot inline, see #2683
        | Get(e, ByKey(FieldKey fi), _, _) ->
            if fi.IsMutable then
                sideEffect <- true
                true
            else findIdentOrSideEffect e
        // We don't have enough information here, so just assume there's a side effect just in case
        | Get(_, ByKey(ExprKey _), _, _) ->
            sideEffect <- true
            true
        | Get(e, (TupleIndex _|UnionField _|UnionTag|ListHead|ListTail|OptionValue _), _, _) ->
            findIdentOrSideEffect e
        | Import _ | Lambda _ | Delegate _ -> false
        // HACK: let beta reduction jump over keyValueList/createObj in Fable.React
        | TypeCast(Call(_,i,_,_),_,Some "optimizable:pojo") ->
            match i.Args with
            | IdentExpr i::_ -> i.Name = identName
            | _ -> false
        | CurriedApply(callee, args, _, _) ->
            callee::args |> findIdentOrSideEffectInList |> orSideEffect
        | Call(e1, info, _, _) ->
            e1 :: (Option.toList info.ThisArg) @ info.Args
            |> findIdentOrSideEffectInList |> orSideEffect
        | Operation(kind, _, _) ->
            match kind with
            | Unary(_, operand) -> findIdentOrSideEffect operand
            | Binary(_, left, right)
            | Logical(_, left, right) -> findIdentOrSideEffect left || findIdentOrSideEffect right
        | Value(value,_) ->
            match value with
            | ThisValue _ | BaseValue _
            | TypeInfo _ | Null _ | UnitConstant | NumberConstant _ | BoolConstant _
            | CharConstant _ | StringConstant _ | RegexConstant _  -> false
            | EnumConstant(e, _) -> findIdentOrSideEffect e
            | NewList(None,_) | NewOption(None,_) -> false
            | NewArrayFrom(e,_)
            | NewOption(Some e,_) -> findIdentOrSideEffect e
            | NewList(Some(h,t),_) -> findIdentOrSideEffect h || findIdentOrSideEffect t
            | NewArray(exprs,_)
            | NewTuple exprs
            | NewUnion(exprs,_,_,_)
            | NewRecord(exprs,_,_)
            | NewAnonymousRecord(exprs,_,_) -> findIdentOrSideEffectInList exprs
        | Sequential exprs -> findIdentOrSideEffectInList exprs
        | Let(_,v,b) -> findIdentOrSideEffect v || findIdentOrSideEffect b
        | TypeCast(e,_,_)
        | Test(e,_,_)
        | Curry(e,_,_,_) -> findIdentOrSideEffect e
        | IfThenElse(cond, thenExpr, elseExpr,_) ->
            findIdentOrSideEffect cond || findIdentOrSideEffect thenExpr || findIdentOrSideEffect elseExpr
        // TODO: Check member bodies in ObjectExpr
        | ObjectExpr _ | LetRec _ | Emit _ | Set _
        | DecisionTree _ | DecisionTreeSuccess _ // Check sub expressions here?
        | WhileLoop _ | ForLoop _ | TryCatch _ ->
            sideEffect <- true
            true

    and findIdentOrSideEffectInList exprs =
        (false, exprs) ||> List.fold (fun result e ->
            result || findIdentOrSideEffect e)

    findIdentOrSideEffect expr && not sideEffect

let canInlineArg identName value body =
    (canHaveSideEffects value |> not && countReferences 1 identName body <= 1)
     || (noSideEffectBeforeIdent identName body
         && isIdentCaptured identName body |> not
         // Make sure is at least referenced once so the expression is not erased
         && countReferences 1 identName body = 1)

module private Transforms =
    let (|LambdaOrDelegate|_|) = function
        | Lambda(arg, body, name) -> Some([arg], body, name)
        | Delegate(args, body, name) -> Some(args, body, name)
        | _ -> None

    let (|FieldKeyType|) (fi: FieldKey) = fi.FieldType

    let (|ImmediatelyApplicable|_|) = function
        | Lambda(arg, body, _) -> Some(arg, body)
        // If the lambda is immediately applied we don't need the closures
        | NestedRevLets(bindings, Lambda(arg, body, _)) ->
            let body = List.fold (fun body (i,v) -> Let(i, v, body)) body bindings
            Some(arg, body)
        | _ -> None

    let lambdaBetaReduction (_com: Compiler) e =
        let applyArgs (args: Ident list) argExprs body =
            let bindings, replacements =
                (([], Map.empty), args, argExprs)
                |||> List.fold2 (fun (bindings, replacements) ident expr ->
                    if canInlineArg ident.Name expr body
                    then bindings, Map.add ident.Name expr replacements
                    else (ident, expr)::bindings, replacements)
            match bindings with
            | [] -> replaceValues replacements body
            | bindings ->
                let body = replaceValues replacements body
                bindings |> List.fold (fun body (i, v) -> Let(i, v, body)) body
        match e with
        // TODO: Other binary operations and numeric types, also recursive?
        | Operation(Binary(AST.BinaryPlus, Value(StringConstant str1, r1), Value(StringConstant str2, r2)),_,_) ->
            Value(StringConstant(str1 + str2), addRanges [r1; r2])
        | Call(Delegate(args, body, _), info, _, _) when List.sameLength args info.Args ->
            applyArgs args info.Args body
        | CurriedApply(applied, argExprs, t, r) ->
            let rec tryImmediateApplication r t applied argExprs =
                match argExprs with
                | [] -> applied
                | argExpr::restArgs ->
                    match applied with
                    | ImmediatelyApplicable(arg, body) ->
                        let applied = applyArgs [arg] [argExpr] body
                        tryImmediateApplication r t applied restArgs
                    | _ -> CurriedApply(applied, argExprs, t, r)
            tryImmediateApplication r t applied argExprs
        | e -> e

    let bindingBetaReduction (com: Compiler) e =
        // Don't erase user-declared bindings in debug mode for better output
        let isErasingCandidate (ident: Ident) =
            (not com.Options.DebugMode) || ident.IsCompilerGenerated
        match e with
        | Let(ident, value, letBody) when (not ident.IsMutable) && isErasingCandidate ident ->
            let canEraseBinding =
                match value with
                | Import(i,_,_) -> i.IsCompilerGenerated
                | NestedLambda(_, lambdaBody, _) ->
                    match lambdaBody with
                    | Import(i,_,_) -> i.IsCompilerGenerated
                    // Check the lambda doesn't reference itself recursively
                    | _ -> countReferences 0 ident.Name lambdaBody = 0
                           && canInlineArg ident.Name value letBody
                | _ -> canInlineArg ident.Name value letBody
            if canEraseBinding then
                let value =
                    match value with
                    // Ident becomes the name of the function (mainly used for tail call optimizations)
                    | Lambda(arg, funBody, _) -> Lambda(arg, funBody, Some ident.Name)
                    | Delegate(args, funBody, _) -> Delegate(args, funBody, Some ident.Name)
                    | value -> value
                replaceValues (Map [ident.Name, value]) letBody
            else e
        | e -> e

    /// Returns arity of lambda (or lambda option) types
    let getLambdaTypeArity t =
        let rec getLambdaTypeArity acc = function
            | LambdaType(_, returnType) ->
                getLambdaTypeArity (acc + 1) returnType
            | t -> acc, t
        match t with
        | LambdaType(_, returnType)
        | Option(LambdaType(_, returnType)) ->
            getLambdaTypeArity 1 returnType
        | _ -> 0, t

    let curryIdentsInBody replacements body =
        visitFromInsideOut (function
            | IdentExpr id as e ->
                match Map.tryFind id.Name replacements with
                | Some arity -> Curry(e, arity, id.Type, id.Range)
                | None -> e
            | e -> e) body

    let uncurryIdentsAndReplaceInBody (idents: Ident list) body =
        let replacements =
            (Map.empty, idents) ||> List.fold (fun replacements id ->
                let arity, _ = getLambdaTypeArity id.Type
                if arity > 1
                then Map.add id.Name arity replacements
                else replacements)
        if Map.isEmpty replacements
        then body
        else curryIdentsInBody replacements body

    let uncurryExpr com arity expr =
        let matches arity arity2 =
            match arity with
            // TODO: check cases where arity <> arity2
            | Some arity -> arity = arity2
            // Remove currying for dynamic operations (no arity)
            | None -> true
        match expr, expr with
        | MaybeCasted(LambdaUncurriedAtCompileTime arity lambda), _ -> lambda
        | _, Curry(innerExpr, arity2,_,_)
            when matches arity arity2 -> innerExpr
        | _, Get(Curry(innerExpr, arity2,_,_), OptionValue, t, r)
            when matches arity arity2 -> Get(innerExpr, OptionValue, t, r)
        | _, Value(NewOption(Some(Curry(innerExpr, arity2,_,_)),r1),r2)
            when matches arity arity2 -> Value(NewOption(Some(innerExpr),r1),r2)
        | _ ->
            match arity with
            | Some arity -> Replacements.uncurryExprAtRuntime com arity expr
            | None -> expr

    // For function arguments check if the arity of their own function arguments is expected or not
    // TODO: Do we need to do this recursively, and check options and delegates too?
    let checkSubArguments com expectedType (expr: Expr) =
        match expectedType, expr with
        | NestedLambdaType(expectedArgs,_), ExprType(NestedLambdaType(actualArgs,_)) ->
            let expectedLength = List.length expectedArgs
            if List.length actualArgs < expectedLength then expr
            else
                let actualArgs = List.truncate expectedLength actualArgs
                let _, replacements =
                    ((0, Map.empty), expectedArgs, actualArgs)
                    |||> List.fold2 (fun (index, replacements) expected actual ->
                        match expected, actual with
                        | GenericParam _, NestedLambdaType(args2, _) when List.isMultiple args2 ->
                            index + 1, Map.add index (0, List.length args2) replacements
                        | NestedLambdaType(args1, _), NestedLambdaType(args2, _)
                                when not(List.sameLength args1 args2) ->
                            let expectedArity = List.length args1
                            let actualArity = List.length args2
                            index + 1, Map.add index (expectedArity, actualArity) replacements
                        | _ -> index + 1, replacements)
                if Map.isEmpty replacements then expr
                else
                    let mappings =
                        actualArgs |> List.mapi (fun i _ ->
                            match Map.tryFind i replacements with
                            | Some (expectedArity, actualArity) ->
                                NewTuple [makeIntConst expectedArity; makeIntConst actualArity] |> makeValue None
                            | None -> makeIntConst 0)
                        |> makeArray Any
                    Replacements.Helper.LibCall(com, "Util", "mapCurriedArgs", expectedType, [expr; mappings])
        | _ -> expr

    let uncurryArgs com autoUncurrying argTypes args =
        let mapArgs f argTypes args =
            let rec mapArgsInner f acc argTypes args =
                match argTypes, args with
                | head1::tail1, head2::tail2 ->
                    let x = f head1 head2
                    mapArgsInner f (x::acc) tail1 tail2
                | [], head2::tail2 when autoUncurrying ->
                    let x = f Any head2
                    mapArgsInner f (x::acc) [] tail2
                | [], args2 -> (List.rev acc)@args2
                | _, [] -> List.rev acc
            mapArgsInner f [] argTypes args
        (argTypes, args) ||> mapArgs (fun expectedType arg ->
            match expectedType with
            | Any when autoUncurrying -> uncurryExpr com None arg
            | _ ->
                let arg = checkSubArguments com expectedType arg
                let arity, _ = getLambdaTypeArity expectedType
                if arity > 1
                then uncurryExpr com (Some arity) arg
                else arg)

    let uncurryInnerFunctions (_: Compiler) e =
        let curryIdentInBody identName (args: Ident list) body =
            curryIdentsInBody (Map [identName, List.length args]) body
        match e with
        | Let(ident, NestedLambdaWithSameArity(args, fnBody, _), letBody) when List.isMultiple args
                                                                          && not ident.IsMutable ->
            let fnBody = curryIdentInBody ident.Name args fnBody
            let letBody = curryIdentInBody ident.Name args letBody
            Let(ident, Delegate(args, fnBody, None), letBody)
        // Anonymous lambda immediately applied
        | CurriedApply(NestedLambdaWithSameArity(args, fnBody, Some name), argExprs, t, r)
                        when List.isMultiple args && List.sameLength args argExprs ->
            let fnBody = curryIdentInBody name args fnBody
            let info = makeCallInfo None argExprs (args |> List.map (fun a -> a.Type))
            Delegate(args, fnBody, Some name)
            |> makeCall r t info
        | e -> e

    let propagateUncurryingThroughLets (_: Compiler) = function
        | Let(ident, value, body) when not ident.IsMutable ->
            let ident, value, arity =
                match value with
                | Curry(innerExpr, arity,_,_) ->
                    ident, innerExpr, Some arity
                | Get(Curry(innerExpr, arity,_,_), OptionValue, t, r) ->
                    ident, Get(innerExpr, OptionValue, t, r), Some arity
                | Value(NewOption(Some(Curry(innerExpr, arity,_,_)),r1),r2) ->
                    ident, Value(NewOption(Some(innerExpr),r1),r2), Some arity
                | _ -> ident, value, None
            match arity with
            | None -> Let(ident, value, body)
            | Some arity ->
                let replacements = Map [ident.Name, arity]
                Let(ident, value, curryIdentsInBody replacements body)
        | e -> e

    let uncurryMemberArgs (m: MemberDecl) =
        if m.Info.IsValue then m
        else { m with Body = uncurryIdentsAndReplaceInBody m.Args m.Body }

    let uncurryReceivedArgs (com: Compiler) e =
        match e with
        // TODO: This breaks cases when we actually need to import a curried function
        // // Sometimes users type imports as lambdas but if they come from JS they're not curried
        // | ExprTypeAs(NestedLambdaType(argTypes, retType), (Import(info, t, r) as e))
        //             when not info.IsCompilerGenerated && List.isMultiple argTypes ->
        //     Curry(e, List.length argTypes, t, r)
        | Lambda(arg, body, name) ->
            let body = uncurryIdentsAndReplaceInBody [arg] body
            Lambda(arg, body, name)
        | Delegate(args, body, name) ->
            let body = uncurryIdentsAndReplaceInBody args body
            Delegate(args, body, name)
        // Uncurry also values received from getters
        | Get(callee, (ByKey(FieldKey(FieldKeyType fieldType)) | UnionField(_,fieldType)), t, r) ->
            match getLambdaTypeArity fieldType, callee.Type with
            // For anonymous records, if the lambda returns a generic the actual
            // arity may be higher than expected, so we need a runtime partial application
            | (arity, GenericParam _), AnonymousRecordType _ when arity > 0 ->
                let callee = makeImportLib com Any "checkArity" "Util"
                let info = makeCallInfo None [makeIntConst arity; e] []
                let e = Call(callee, info, t, r)
                if arity > 1 then Curry(e, arity, t, r)
                else e
            | (arity, _), _ when arity > 1 -> Curry(e, arity, t, r)
            | _ -> e
        | ObjectExpr(members, t, baseCall) ->
            ObjectExpr(List.map uncurryMemberArgs members, t, baseCall)
        | e -> e

    let uncurrySendingArgs (com: Compiler) e =
        let uncurryConsArgs args (fields: seq<Field>) =
            let argTypes =
                fields
                |> Seq.map (fun fi -> fi.FieldType)
                |> Seq.toList
            uncurryArgs com false argTypes args
        match e with
        | Call(callee, info, t, r) ->
            let args = uncurryArgs com false info.SignatureArgTypes info.Args
            let info = { info with Args = args }
            Call(callee, info, t, r)
        | CurriedApply(callee, args, t, r) ->
            match callee.Type with
            | NestedLambdaType(argTypes, _) ->
                CurriedApply(callee, uncurryArgs com false argTypes args, t, r)
            | _ -> e
        | Emit({ CallInfo = callInfo } as emitInfo, t, r) ->
            let args = uncurryArgs com true callInfo.SignatureArgTypes callInfo.Args
            Emit({ emitInfo with CallInfo = { callInfo with Args = args } }, t, r)
        // Uncurry also values in setters or new record/union/tuple
        | Value(NewRecord(args, ent, genArgs), r) ->
            let args = com.GetEntity(ent).FSharpFields |> uncurryConsArgs args
            Value(NewRecord(args, ent, genArgs), r)
        | Value(NewAnonymousRecord(args, fieldNames, genArgs), r) ->
            let args = uncurryArgs com false genArgs args
            Value(NewAnonymousRecord(args, fieldNames, genArgs), r)
        | Value(NewUnion(args, tag, ent, genArgs), r) ->
            let uci = com.GetEntity(ent).UnionCases.[tag]
            let args = uncurryConsArgs args uci.UnionCaseFields
            Value(NewUnion(args, tag, ent, genArgs), r)
        | Set(e, Some(FieldKey fi), value, r) ->
            let value = uncurryArgs com false [fi.FieldType] [value]
            Set(e, Some(FieldKey fi), List.head value, r)
        | ObjectExpr(members, t, baseCall) ->
            let membersMap =
                match t with
                | DeclaredType(e, _genArgs) ->
                    com.GetEntity(e).MembersFunctionsAndValues
                    |> Seq.choose (fun m ->
                        if m.IsGetter || m.IsValue then
                            Some(m.CompiledName, m.ReturnParameter.Type)
                        else None)
                    |> Map
                | _ -> Map.empty
            let members =
                members |> List.map (fun m ->
                    let hasGenerics = m.Body.Type.Generics |> List.isEmpty |> not
                    if m.Info.IsGetter || (m.Info.IsValue && not hasGenerics) then
                        let membType =
                            Map.tryFind m.Name membersMap
                            |> Option.defaultValue m.Body.Type
                        let value = uncurryArgs com false [membType] [m.Body]
                        { m with Body = List.head value }
                    else m)
            ObjectExpr(members, t, baseCall)
        | e -> e

    let rec uncurryApplications (com: Compiler) e =
        let uncurryApply r t applied args uncurriedArity =
            let argsLen = List.length args
            if uncurriedArity = argsLen then
                // This is already uncurried we don't need the signature arg types anymore,
                // just make a normal call
                let info = makeCallInfo None args []
                makeCall r t info applied |> Some
            else
                Replacements.partialApplyAtRuntime com t (uncurriedArity - argsLen) applied args |> Some
        match e with
        | NestedApply(applied, args, t, r) ->
            let applied = visitFromOutsideIn (uncurryApplications com) applied
            let args = args |> List.map (visitFromOutsideIn (uncurryApplications com))
            match applied with
            | Curry(applied, uncurriedArity,_,_) ->
                uncurryApply r t applied args uncurriedArity
            | Get(Curry(applied, uncurriedArity,_,_), OptionValue, t2, r2) ->
                uncurryApply r t (Get(applied, OptionValue, t2, r2)) args uncurriedArity
            | _ -> CurriedApply(applied, args, t, r) |> Some
        | _ -> None

open Transforms

// ATTENTION: Order of transforms matters
// TODO: Optimize binary operations with numerical or string literals
let getTransformations (_com: Compiler) =
    [ // First apply beta reduction
      fun com e -> visitFromInsideOut (bindingBetaReduction com) e
      fun com e -> visitFromInsideOut (lambdaBetaReduction com) e
      // Make an extra binding reduction pass after applying lambdas
      fun com e -> visitFromInsideOut (bindingBetaReduction com) e
      // Then apply uncurry optimizations
      fun com e -> visitFromInsideOut (uncurryReceivedArgs com) e
      fun com e -> visitFromInsideOut (uncurryInnerFunctions com) e
      fun com e -> visitFromInsideOut (propagateUncurryingThroughLets com) e
      fun com e -> visitFromInsideOut (uncurrySendingArgs com) e
      // uncurryApplications must come after uncurrySendingArgs as it erases argument type info
      fun com e -> visitFromOutsideIn (uncurryApplications com) e
    ]

let transformDeclaration transformations (com: Compiler) file decl =
    let transformExpr (com: Compiler) e =
        List.fold (fun e f -> f com e) e transformations

    let transformMemberBody com (m: MemberDecl) =
        { m with Body = transformExpr com m.Body }

    match decl with
    | ActionDeclaration decl ->
        { decl with Body = transformExpr com decl.Body }
        |> ActionDeclaration

    | MemberDeclaration m ->
        com.ApplyMemberDeclarationPlugin(file, m)
        |> uncurryMemberArgs
        |> transformMemberBody com
        |> MemberDeclaration

    | ClassDeclaration decl ->
        // (ent, ident, cons, baseCall, attachedMembers)
        let attachedMembers =
            decl.AttachedMembers
            |> List.map (uncurryMemberArgs >> transformMemberBody com)

        let cons, baseCall =
            match decl.Constructor, decl.BaseCall with
            | None, _ -> None, None
            | Some cons, None ->
                uncurryMemberArgs cons |> transformMemberBody com |> Some, None
            | Some cons, Some baseCall ->
                // In order to uncurry correctly the baseCall arguments,
                // we need to include it in the constructor body
                Sequential [baseCall; cons.Body]
                |> uncurryIdentsAndReplaceInBody cons.Args
                |> transformExpr com
                |> function
                    | Sequential [baseCall; body] -> Some { cons with Body = body }, Some baseCall
                    | body -> Some { cons with Body = body }, None // Unexpected, raise error?

        { decl with Constructor = cons
                    BaseCall = baseCall
                    AttachedMembers = attachedMembers }
        |> ClassDeclaration

let transformFile (com: Compiler) (file: File) =
    let transformations = getTransformations com
    let newDecls = List.map (transformDeclaration transformations com file) file.Declarations
    File(newDecls, usedRootNames=file.UsedNamesInRootScope)
