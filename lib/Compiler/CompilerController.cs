namespace Flow
{
	internal sealed class CompilerController
	{
		public readonly Compiler compiler = new Compiler();
		public Buffer<Source> compiledSources = new Buffer<Source>(1);

		private readonly ParseRules parseRules = new ParseRules();

		public Buffer<CompileError> CompileSource(ByteCodeChunk chunk, Source source)
		{
			compiledSources.ZeroReset();

			compiler.Reset(chunk);
			Compile(source);
			return compiler.errors;
		}

		private void Compile(Source source)
		{
			compiler.BeginSource(source.content, compiledSources.count);
			compiledSources.PushBack(source);

			//var finishedModuleImports = false;
			compiler.parser.Next();
			while (!compiler.parser.Match(TokenKind.End))
				Statement();

			compiler.EndSource();
		}

		private void Syncronize()
		{
			if (!compiler.isInPanicMode)
				return;

			while (compiler.parser.currentToken.kind != TokenKind.End)
			{
				if (compiler.parser.currentToken.kind != TokenKind.SemiColon)
					compiler.parser.Next();

				compiler.isInPanicMode = false;
				break;
			}
		}

		private void Statement()
		{
			if (compiler.parser.Match(TokenKind.External))
				ExternalCommandStatement();
			else if (compiler.parser.Match(TokenKind.If))
				IfStatement();
			else if (compiler.parser.Match(TokenKind.Iterate))
				IterateStatement();
			else
				ExpressionStatement();
		}

		private void ExternalCommandStatement()
		{
			compiler.parser.Consume(TokenKind.Identifier, new CompileErrors.ExternalCommands.ExpectedExternalCommandIdentifier());
			var nameSlice = compiler.parser.previousToken.slice;
			var name = CompilerHelper.GetSlice(compiler, nameSlice);

			compiler.parser.Consume(TokenKind.IntLiteral, new CompileErrors.ExternalCommands.ExpectedExternalCommandParameterCount());
			var parameterCount = CompilerHelper.GetParsedInt(compiler);
			if (parameterCount > byte.MaxValue)
			{
				compiler.AddSoftError(compiler.parser.previousToken.slice, new CompileErrors.ExternalCommands.TooManyExternalCommandParameters());
				parameterCount = byte.MaxValue;
			}

			compiler.parser.Consume(TokenKind.IntLiteral, new CompileErrors.ExternalCommands.ExpectedExternalCommandReturnCount());
			var returnCount = CompilerHelper.GetParsedInt(compiler);
			if (returnCount > byte.MaxValue)
			{
				compiler.AddSoftError(compiler.parser.previousToken.slice, new CompileErrors.ExternalCommands.TooManyExternalCommandReturnValues());
				returnCount = byte.MaxValue;
			}

			compiler.parser.Consume(TokenKind.SemiColon, new CompileErrors.General.ExpectedSemiColonAfterStatement());

			var success = compiler.chunk.AddExternalCommand(new ExternalCommandDefinition(name, (byte)parameterCount, (byte)returnCount));
			if (!success)
				compiler.AddSoftError(nameSlice, new CompileErrors.Commands.CommandNameAlreadyRegistered { name = name });
		}

		private void IfStatement()
		{
			var expression = Expression(false);
			if (expression.valueCount != 1)
			{
				compiler.AddSoftError(expression.slice, new CompileErrors.If.ExprectedOneValueAsIfCondition
				{
					got = expression.valueCount
				});
			}

			var elseJump = compiler.BeginEmitForwardJump(Instruction.PopAndJumpForwardIfFalse);

			compiler.parser.Consume(TokenKind.OpenCurlyBrackets, new CompileErrors.If.ExpectedOpenCurlyBracesAfterIfCondition());
			Block();

			var thenJump = compiler.BeginEmitForwardJump(Instruction.JumpForward);
			compiler.EndEmitForwardJump(elseJump);

			if (compiler.parser.Match(TokenKind.Else))
			{
				if (compiler.parser.Match(TokenKind.If))
				{
					IfStatement();
				}
				else
				{
					compiler.parser.Consume(TokenKind.OpenCurlyBrackets, new CompileErrors.If.ExpectedOpenCurlyBracesAfterElse());
					Block();
				}
			}

			compiler.EndEmitForwardJump(thenJump);
		}

		private void IterateStatement()
		{
			var loopJump = compiler.BeginEmitBackwardJump();

			var scope = compiler.BeginScope();
			var expression = Expression(false);
			if (expression.valueCount != 1)
			{
				compiler.AddSoftError(expression.slice, new CompileErrors.Iterate.ExpectedOneValueAsIterateCondition
				{
					got = expression.valueCount
				});
			}

			var breakJump = compiler.BeginEmitForwardJump(Instruction.JumpForwardIfNull);

			compiler.AddLocalVariable(default, LocalVariableFlag.Input);

			compiler.parser.Consume(TokenKind.OpenCurlyBrackets, new CompileErrors.Iterate.ExpectedOpenCurlyBracesAfterIterateCondition());
			Block();

			compiler.EndScope(scope);
			compiler.EndEmitBackwardJump(Instruction.JumpBackward, loopJump);
			compiler.EndEmitForwardJump(breakJump);

			compiler.EmitPop(1);
		}

		private void ExpressionStatement()
		{
			var expression = Expression(true);

			compiler.parser.Next();
			if (compiler.parser.previousToken.kind != TokenKind.SemiColon)
				compiler.AddHardError(expression.slice, new CompileErrors.General.ExpectedSemiColonAfterStatement());

			compiler.EmitPop(expression.valueCount);
		}

		private bool TryParseWithPrecedence(Precedence precedence, out ExpressionResult result)
		{
			var parser = compiler.parser;
			var slice = parser.currentToken.slice;
			if (parser.currentToken.kind == TokenKind.End)
			{
				parser.Next();
				result = new ExpressionResult(slice, 0);
				return true;
			}

			var prefixRule = parseRules.GetPrefixRule(parser.currentToken.kind);
			if (prefixRule == null)
			{
				result = new ExpressionResult(slice, 0);
				return false;
			}

			parser.Next();
			var valueCount = prefixRule(this);

			while (
				parser.currentToken.kind != TokenKind.End &&
				precedence <= parseRules.GetPrecedence(parser.currentToken.kind)
			)
			{
				parser.Next();
				var infixRule = parseRules.GetInfixRule(parser.previousToken.kind);
				valueCount = infixRule(this, new ExpressionResult(slice, valueCount));
				slice = Slice.FromTo(slice, parser.previousToken.slice);
			}

			slice = Slice.FromTo(slice, parser.previousToken.slice);
			result = new ExpressionResult(slice, valueCount);
			return true;
		}

		private ExpressionResult ParseWithPrecedence(Precedence precedence)
		{
			if (!TryParseWithPrecedence(precedence, out var result))
			{
				compiler.AddHardError(result.slice, new CompileErrors.General.ExpectedExpression());
			}

			return result;
		}

		private ExpressionResult Expression(bool canAssignToVariable)
		{
			var expression = ParseWithPrecedence(Precedence.Expression);

			if (compiler.parser.Match(TokenKind.Pipe))
			{
				var valueCount = Pipe(expression, canAssignToVariable);
				expression = new ExpressionResult(
					Slice.FromTo(
						expression.slice,
						compiler.parser.previousToken.slice
					),
					valueCount
				);
			}

			return expression;
		}

		private bool TryValue(out ExpressionResult result)
		{
			return TryParseWithPrecedence(Precedence.Primary, out result);
		}

		private void Block()
		{
			var scope = compiler.BeginScope();
			while (
				!compiler.parser.Check(TokenKind.End) &&
				!compiler.parser.Check(TokenKind.CloseCurlyBrackets)
			)
			{
				Statement();
			}

			compiler.parser.Consume(TokenKind.CloseCurlyBrackets, new CompileErrors.Block.ExpectedCloseCurlyBracketsAfterBlock());
			compiler.EndScope(scope);
		}

		internal static byte Group(CompilerController self)
		{
			var expression = self.Expression(false);
			self.compiler.parser.Consume(TokenKind.CloseParenthesis, new CompileErrors.Group.ExpectedCloseParenthesisAfterExpression());
			return expression.valueCount;
		}

		private byte Pipe(ExpressionResult previous, bool canAssignToVariable)
		{
			var valueCount = previous.valueCount;

			while (!compiler.parser.Check(TokenKind.End))
			{
				compiler.parser.Next();
				switch (compiler.parser.previousToken.kind)
				{
				case TokenKind.Comma:
					valueCount += ParseWithPrecedence(Precedence.Comma).valueCount;
					if (valueCount > byte.MaxValue)
					{
						var slice = Slice.FromTo(previous.slice, compiler.parser.previousToken.slice);
						compiler.AddSoftError(slice, new CompileErrors.Comma.TooManyExpressionValues());
						valueCount = byte.MaxValue;
					}
					break;
				case TokenKind.Variable:
					AssignLocals(canAssignToVariable, valueCount);
					return 0;
				case TokenKind.Identifier:
					valueCount = PipedCommand(valueCount);
					break;
				default:
					compiler.AddHardError(compiler.parser.previousToken.slice, new CompileErrors.Pipe.InvalidTokenAfterPipe());
					return 0;
				}

				if (!compiler.parser.Match(TokenKind.Pipe))
					break;
			}

			return valueCount;
		}

		internal static byte Comma(CompilerController self, ExpressionResult previous)
		{
			var expression = self.ParseWithPrecedence(Precedence.Comma + 1);
			var valueCount = previous.valueCount + expression.valueCount;

			if (valueCount > byte.MaxValue)
			{
				var slice = Slice.FromTo(previous.slice, expression.slice);
				self.compiler.AddSoftError(slice, new CompileErrors.Comma.TooManyExpressionValues());
				return byte.MaxValue;
			}

			return (byte)valueCount;
		}

		internal static byte Command(CompilerController self)
		{
			return self.PipedCommand(0);
		}

		private byte PipedCommand(byte valueCount)
		{
			var commandSlice = compiler.parser.previousToken.slice;
			var slice = commandSlice;

			var argCount = 0;
			while (TryValue(out var valueResult))
			{
				slice = Slice.FromTo(slice, valueResult.slice);
				argCount += valueResult.valueCount;
			}

			var commandIndex = -1;
			for (var i = 0; i < compiler.chunk.commandDefinitions.count; i++)
			{
				var command = compiler.chunk.commandDefinitions.buffer[i];
				if (CompilerHelper.AreEqual(
					compiler.parser.tokenizer.source,
					commandSlice,
					command.name
				))
				{
					commandIndex = i;
					break;
				}
			}

			if (commandIndex < 0)
			{
				compiler.AddSoftError(slice, new CompileErrors.Commands.CommandNotRegistered { name = CompilerHelper.GetSlice(compiler, commandSlice) });
				return 0;
			}
			else
			{
				var command = compiler.chunk.commandDefinitions.buffer[commandIndex];
				if (argCount != command.parameterCount)
				{
					compiler.AddSoftError(slice, new CompileErrors.Commands.WrongNumberOfCommandArguments
					{
						commandName = command.name,
						expected = command.parameterCount,
						got = argCount
					});
				}
				else
				{
					compiler.EmitCallNativeCommand(commandIndex, valueCount);
				}

				return command.returnCount;
			}
		}

		private void AssignLocals(bool canAssign, byte valueCount)
		{
			var slice = compiler.parser.previousToken.slice;

			var slices = new Buffer<Slice>(1);
			slices.PushBackUnchecked(slice);

			var isAssignment = compiler.ResolveToLocalVariableIndex(slice, out var _);
			var hasMixedAssignmentType = false;

			while (compiler.parser.Match(TokenKind.Comma))
			{
				compiler.parser.Consume(TokenKind.Variable, new CompileErrors.Variables.ExpectedVariableAsAssignmentTarget());

				var varSlice = compiler.parser.previousToken.slice;
				slices.PushBackUnchecked(varSlice);

				var isAnotherAssignment = compiler.ResolveToLocalVariableIndex(varSlice, out var _);
				if (isAssignment != isAnotherAssignment)
					hasMixedAssignmentType = true;
			}

			slice = Slice.FromTo(slices.buffer[0], compiler.parser.previousToken.slice);

			if (!canAssign)
			{
				compiler.AddSoftError(slice, new CompileErrors.Variables.CanOnlyAssignToVariablesAtTopLevelExpressions());
			}
			else if (slices.count != valueCount)
			{
				compiler.AddSoftError(slice, new CompileErrors.Variables.WrongNumberOfVariablesOnAssignment
				{
					expected = valueCount,
					got = slices.count
				});
			}
			else if (hasMixedAssignmentType)
			{
				compiler.AddSoftError(slice, new CompileErrors.Variables.MixedAssignmentType());
			}
			else if (isAssignment)
			{
				for (var i = slices.count - 1; i >= 0; i--)
				{
					var varSlice = slices.buffer[i];
					compiler.ResolveToLocalVariableIndex(varSlice, out var localIndex);
					compiler.EmitInstruction(Instruction.AssignLocal);
					compiler.EmitByte(localIndex);
				}
			}
			else
			{
				for (var i = 0; i < slices.count; i++)
				{
					var varSlice = slices.buffer[i];
					if (compiler.localVariables.count >= byte.MaxValue)
						compiler.AddSoftError(varSlice, new CompileErrors.Variables.TooManyVariables());
					compiler.AddLocalVariable(varSlice, LocalVariableFlag.Unused);
				}
			}
		}

		internal static byte LoadLocal(CompilerController self)
		{
			var slice = self.compiler.parser.previousToken.slice;

			if (self.compiler.ResolveToLocalVariableIndex(slice, out var localIndex))
			{
				ref var flag = ref self.compiler.localVariables.buffer[localIndex].flag;
				if (flag == LocalVariableFlag.Unused)
					flag = LocalVariableFlag.Used;

				self.compiler.EmitInstruction(Instruction.LoadLocal);
				self.compiler.EmitByte(localIndex);
			}
			else
			{
				self.compiler.AddSoftError(slice, new CompileErrors.Variables.LocalVariableUnassigned { name = CompilerHelper.GetSlice(self.compiler, slice) });
			}

			return 1;
		}

		internal static byte Literal(CompilerController self)
		{
			switch (self.compiler.parser.previousToken.kind)
			{
			case TokenKind.False:
				self.compiler.EmitInstruction(Instruction.LoadFalse);
				break;
			case TokenKind.True:
				self.compiler.EmitInstruction(Instruction.LoadTrue);
				break;
			case TokenKind.IntLiteral:
				self.compiler.EmitLoadLiteral(new Value(CompilerHelper.GetParsedInt(self.compiler)));
				break;
			case TokenKind.FloatLiteral:
				self.compiler.EmitLoadLiteral(new Value(CompilerHelper.GetParsedFloat(self.compiler)));
				break;
			case TokenKind.StringLiteral:
				self.compiler.EmitLoadLiteral(new Value(CompilerHelper.GetParsedString(self.compiler)));
				break;
			default:
				self.compiler.AddHardError(self.compiler.parser.previousToken.slice, new CompileErrors.Literals.ExpectedLiteral { got = self.compiler.parser.previousToken.kind });
				break;
			}

			return 1;
		}
	}
}