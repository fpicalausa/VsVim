﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vim;
using Moq;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;

namespace VimCoreTest.Utils
{
    internal static class MockObjectFactory
    {
        internal static Mock<IRegisterMap> CreateRegisterMap()
        {
            var mock = new Mock<IRegisterMap>();
            var reg = new Register('_');
            mock.Setup(x => x.DefaultRegisterName).Returns('_');
            mock.Setup(x => x.DefaultRegister).Returns(reg);
            return mock;
        }

        internal static Mock<IVimData> CreateVimData(
            IRegisterMap registerMap = null,
            MarkMap map = null,
            VimSettings settings = null)
        {
            registerMap = registerMap ?? CreateRegisterMap().Object;
            map = map ?? new MarkMap();
            settings = settings ?? VimSettingsUtil.CreateDefault;
            var mock = new Mock<IVimData>(MockBehavior.Strict);
            mock.Setup(x => x.RegisterMap).Returns(registerMap);
            mock.Setup(x => x.MarkMap).Returns(map);
            mock.Setup(x => x.Settings).Returns(settings);
            return mock;
        }

        internal static Mock<IVim> CreateVim(
            IVimData data = null)
        {
            data = data ?? CreateVimData().Object;
            var mock = new Mock<IVim>(MockBehavior.Strict);
            mock.Setup(x => x.Data).Returns(data);
            return mock;
        }

        internal static Mock<IBlockCaret> CreateBlockCaret()
        {
            var mock = new Mock<IBlockCaret>(MockBehavior.Loose);
            return mock;
        }

        internal static Mock<IEditorOperations> CreateEditorOperations()
        {
            var mock = new Mock<IEditorOperations>(MockBehavior.Strict);
            return mock;
        }

        internal static VimBufferData CreateVimBufferData(
            IWpfTextView view, 
            string name = null,
            IVimHost host = null, 
            IVimData data = null,
            IBlockCaret caret = null,
            IEditorOperations editorOperations = null)
        {
            name = name ?? "test";
            host = host ?? new FakeVimHost();
            data = data ?? CreateVimData().Object;
            caret = caret ?? CreateBlockCaret().Object;
            editorOperations = editorOperations ?? CreateEditorOperations().Object;
            return new VimBufferData("test", view, host, data, caret, editorOperations);
        }
    }
}
