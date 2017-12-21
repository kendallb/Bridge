/**
 * @compiler Bridge.NET 16.6.0
 */
Bridge.assembly("TestProject", function ($asm, globals) {
    "use strict";

    Bridge.define("TestProject1.TestClassA", {
        fields: {
            Value1: 0
        }
    });

    Bridge.define("TestProject2.TestClassB", {
        fields: {
            Value1: 0
        }
    });
});
