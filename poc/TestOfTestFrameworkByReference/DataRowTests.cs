﻿//
// Copyright (c) .NET Foundation and Contributors
// Portions Copyright (c) Microsoft Corporation.  All rights reserved.
// See LICENSE file in the project root for full license information.
//

using System;
using System.Diagnostics;
using System.Threading;

namespace nanoFramework.TestFramework.Test
{
    [TestClass]
    public class TestOfDataRow
    {
        [TestMethod]
        [DataRow(1, 2, 3)]
        [DataRow(5, 6, 11)]
        public void TestAddition(int number1, int number2, int result)
        {
            var additionResult = number1 + number2;

            Assert.Equal(additionResult, result);
        }

        [TestMethod]
        [DataRow("TestString")]
        public void TestString(string testData)
        {
            Assert.Equal(testData, "TestString");
        }

        [TestMethod]
        [DataRow("adsdasdasasddassaadsdasdasasddassaadsdasdasasddassaadsdasdasasddassaadsdasdasasddassaadsdasdasasddassa")]
        public void TestLongString(string testData)
        {
            Assert.Equal(testData, "adsdasdasasddassaadsdasdasasddassaadsdasdasasddassaadsdasdasasddassaadsdasdasasddassaadsdasdasasddassa");
        }

        [TestMethod]
        [DataRow("Right align in 10 chars: {0,10:N2}: and then more", 1234.5641, "Right align in 10 chars:   1,234.56: and then more")]
        public void TestStringWithComma(string formatString, double value, string outcomeMessage)
        {
            // Test alignment operator which is the "," and a number. Negative is right aligned, positive left aligned
            Assert.Equal(string.Format(formatString, value), outcomeMessage);
        }
    }
}
