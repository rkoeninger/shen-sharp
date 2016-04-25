﻿namespace Kl

open Extensions

module Evaluator =

    let rec go = function
        | Pending thunk -> thunk.Run() |> go
        | Done result -> result

    let private append env defs = {env with Locals = List.Cons(Map.ofList defs, env.Locals)}

    let (>>=) result f =
        match result with
        | Ok value -> f value
        | Err _ as error -> Done error

    let (>>>=) result f =
        match result with
        | Ok value -> f value |> Done
        | Err _ as error -> error |> Done

    let rec private resolveLocalSymbol locals id =
        match locals with
        | [] -> None
        | frame :: rest ->
            match Map.tryFind id frame with
            | Some(value) -> Some(value)
            | None -> resolveLocalSymbol rest id

    // For symbols not in operator position:
    // Those starting with upper-case letter are idle if not defined.
    // Those not starting with upper-case letter are always idle.
    let private resolveSymbol env id =
        if Values.isVar id then
            match resolveLocalSymbol env.Locals id with
            | Some value -> value
            | None -> SymbolValue id
        else
            SymbolValue id

    // For symbols in operator position:
    // Those starting with upper-case letter are resolved using the local stack.
    // Those not starting with upper-case letter are resolved using the global function namespace.
    // Symbols in operator position are never idle.
    let private resolveFunction env id =
        if Values.isVar id then
            match resolveLocalSymbol env.Locals id with
            | Some value ->
                match value with
                | FunctionValue f -> Ok f
                | _ -> Err "Symbol does not represent a function"
            | None -> Err("Symbol not defined: " + id)
        else
            match env.Globals.Functions.GetMaybe id with
            | Some f -> Ok f
            | None -> Err("Symbol not defined: " + id)

    let private vbranch ifTrue ifFalse value =
        if Values.vbool value
            then ifTrue()
            else ifFalse()
            
    let rec apply pos globals f args =
        let env = {Globals = globals; Locals = []}

        match f with
        | Freeze(locals, body) ->
            match args with
            | [] -> evalw {env with Locals = locals} body
            | _ -> Done(Err "Freezes do not take arguments")
        | Lambda(param, locals, body) as lambda ->
            match args with
            | [] -> Done(Ok(FunctionValue(lambda)))
            | [x] -> evalw (append env [(param, x)]) body
            | _ -> Done(Err "Lambdas take exactly 1 argument")
        | Defun(_, paramz, body) as defun ->
            match args with
            | [] -> Done(Ok(FunctionValue(defun)))
            | _ ->
                match args.Length, paramz.Length with
                | Greater -> Done(Err "Too many arguments")
                | Lesser -> Done(Ok(FunctionValue(Partial(args, defun))))
                | Equal ->
                    let env = (append env (List.zip paramz args))
                    match pos with
                    | Head -> evalw env body
                    | Tail -> Values.thunkw(fun () -> evalw env body)
        | Primitive(_, arity, f) as primitive ->
            match args with
            | [] -> Done(Ok(FunctionValue primitive))
            | _ ->
                match args.Length, arity with
                | Greater -> Done(Err "Too many arguments")
                | Lesser -> Done(Ok(FunctionValue(Partial(args, primitive))))
                | Equal ->
                    match pos with
                    | Head -> Done(f globals args)
                    | Tail -> Values.thunkw(fun () -> Done(f globals args))
        | Partial(args0, f) as partial ->
            match args with
            | [] -> Done(Ok(FunctionValue(partial)))
            | _ -> apply pos globals f (List.append args0 args)

    and private evalw env expr =
        let evale = eval env
        let evalwe = evalw env

        match expr with

        // Atomic values besides symbols are self-evaluating
        | EmptyExpr     -> EmptyValue     |> Ok |> Done
        | BoolExpr b    -> BoolValue b    |> Ok |> Done
        | IntExpr n     -> IntValue n     |> Ok |> Done
        | DecimalExpr n -> DecimalValue n |> Ok |> Done
        | StringExpr s  -> StringValue s  |> Ok |> Done

        // Should only get here in the case of symbols not in operator position
        | SymbolExpr s  -> resolveSymbol env s |> Ok |> Done

        // And/Or expressions are lazily evaluated

        // When the first expression evaluates to false,
        // false is the result without evaluating the second expression
        // The first expression must evaluate to a boolean value
        | AndExpr(left, right) ->
            evale left >>= vbranch (fun () -> evalwe right) (fun () -> Values.falsew)
            
        // When the first expression evaluates to true,
        // true is the result without evaluating the second expression
        // The first expression must evaluate to a boolean value
        | OrExpr(left, right) ->
            evale left >>= vbranch (fun () -> Values.truew) (fun () -> evalwe right)

        // If expressions selectively evaluate depending on the result
        // of evaluating the condition expression
        // The condition must evaluate to a boolean value
        | IfExpr (condition, consequent, alternative) ->
            evale condition >>= vbranch (fun () -> evalwe consequent) (fun () -> evalwe alternative)
        
        // Condition expressions must evaluate to boolean values
        | CondExpr clauses ->
            let rec evalClauses = function
                | [] -> Done(Err "No condition was true")
                | (condition, consequent) :: rest ->
                    evale condition >>= vbranch (fun () -> evalwe consequent) (fun () -> evalClauses rest)
            evalClauses clauses

        | LetExpr (symbol, binding, body) ->
            let evalBody v = evalw (append env [symbol, v]) body
            eval env binding >>= evalBody

        // Evaluating a lambda captures the local state, the lambda parameter name and the body expression
        | LambdaExpr (param, body) ->
            Lambda(param, env.Locals, body) |> FunctionValue |> Ok |> Done

        // Evaluating a freeze just captures the local state and the body expression
        | FreezeExpr expr ->
            Freeze(env.Locals, expr) |> FunctionValue |> Ok |> Done

        // Handler expression is not evaluated unless body results in an error
        // Handler expression must evaluate to a function
        | TrapExpr (pos, body, handler) ->
            match evale body with
            | Err e ->
                match evale handler with
                | Ok(FunctionValue f) -> apply pos env.Globals f [ErrorValue e]
                | Ok(_) -> Done(Err "Trap handler did not evaluate to a function")
                | Err message -> Done(Err message)
            | r -> Done r

        // Applications expect a symbol to be in operator position
        // That symbol must evaluate to a function
        // If evaluating any of the argument expressions results in an error,
        // then the application expression results in that error without
        // the function being applied
        | AppExpr (pos, f, args) ->
            match f with
            | SymbolExpr s ->
                match resolveFunction env s with
                | Ok f ->
                    let rec evalArgs args =
                        match args with
                        | [] -> Ok []
                        | arg :: rest ->
                            match evale arg with
                            | Ok value ->
                                match evalArgs rest with
                                | Ok values -> Ok(value :: values)
                                | Err _ as error -> error
                            | Err message -> Err message

                    match evalArgs args with
                    | Ok argsv -> apply pos env.Globals f argsv
                    | Err message -> Done(Err message)
                | Err message -> Done(Err message)
            | _ -> Done(Err "Application must begin with a symbol")
            
    and eval env expr = evalw env expr |> go

    let rootEval globals expr =
        let env = {Globals = globals; Locals = []}

        match expr with
        | DefunExpr(name, paramz, body) ->
            let f = Defun(name, paramz, body)
            globals.Functions.[name] <- f
            Ok(FunctionValue f)

        | OtherExpr expr -> eval env expr
