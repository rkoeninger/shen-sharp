﻿namespace Kl.Tests

open Fantomas
open Fantomas.FormatConfig
open NUnit.Framework
open Kl
open Kl.Values
open Kl.Startup
open Kl.Make.Reader
open Kl.Make.Compiler
open Assertions

[<TestFixture>]
type Compilation() =

    let fn globals name args body =
        let fref = internSymbol name globals.Functions
        fref.Value <- Some(Defun(name, List.length args, InterpretedDefun(args, RawExpr(read body))))

    let sy globals name value =
        globals.Symbols.[name] <- value

    [<Test>]
    member this.``test parse``() =
        let text = "
[<System.Runtime.CompilerServices.Extension>]
module Shen.Runtime
    [<System.Runtime.CompilerServices.Extension>]
    let Load(globals: Globals, path: string) = kl_load globals [Str path] |> ignore
"
        let ast = CodeFormatter.Parse("./test.fs", text)
        Assert.IsTrue(CodeFormatter.IsValidAST ast)
        printfn "%A" ast

    [<Test>]
    member this.``test build``() =
        let globals = baseGlobals()
        fn globals "xor" ["X"; "Y"] "(and (not (and X Y)) (or X Y))"
        fn globals "factorial" ["N"] "(if (= N 0) 1 (* N (factorial (- N 1))))"
        fn globals "inc-all" ["Xs"] "(map (lambda X (+ 1 X)) Xs)"
        sy globals "array-value" (Vec [|Int 1; Int 2; Int 3|])
        sy globals "cons-test" (Cons(Int 1, Int 2))
        sy globals "lambda-test" (Func(Lambda(InterpretedLambda(Map.empty, "X", RawExpr <| toCons [Sym "+"; Sym "X"; Int 1]))))
        let ast = compile ["Shen"; "Runtime"] globals
        let format = {FormatConfig.Default with PageWidth = 1024}
        printfn "%s" (CodeFormatter.FormatAST(ast, "file", None, format))
        printfn "%A" ast
